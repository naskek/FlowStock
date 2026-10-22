#!/usr/bin/env python3
"""Fail-closed operator restore. No scheduler, retention, deploy or API integration."""
from __future__ import annotations

import argparse
import contextlib
import fcntl
import json
import os
from pathlib import Path
import re
import signal
import stat
import subprocess
import sys
import uuid

# Storage utilities only; managed catalog/worker wiring belongs to #122.
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "backup"))
from managed_backup import (BackupError, _archive_info, _directory, _file,
                            _fsync_dir, _read, _rename_new, _write_new, utc_now)


class RestoreError(Exception):
    pass


def require(ok, message):
    if not ok:
        raise RestoreError(message)


def directory(path, private=True):
    path = Path(os.path.abspath(path))
    for parent in reversed([path, *path.parents]):
        if parent.exists() or parent.is_symlink():
            require(not parent.is_symlink(), "unsafe directory symlink")
    path.mkdir(mode=0o700, parents=True, exist_ok=True)
    _directory(path, create=private)
    require(not (path.stat().st_mode & 0o022), "directory writable by other users")
    return path


def snapshot(source, target):
    # Validate/restore the private copy, never reopen the operator's input path.
    fd = os.open(source, os.O_RDONLY | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(fd, "rb") as src:
        before = os.fstat(src.fileno())
        require(stat.S_ISREG(before.st_mode) and before.st_size > 0,
                "input must be a nonempty regular custom-format dump")
        with target.open("xb") as dst:
            os.chmod(target, 0o600)
            while block := src.read(1024 * 1024):
                dst.write(block)
            dst.flush()
            os.fsync(dst.fileno())
        after = os.fstat(src.fileno())
        require((before.st_size, before.st_mtime_ns, before.st_ctime_ns) ==
                (after.st_size, after.st_mtime_ns, after.st_ctime_ns),
                "input changed during snapshot")
    _fsync_dir(target.parent)


class DockerPostgres:
    services = ("discovery-relay", "flowstock", "nginx", "pgbackup")

    def __init__(self):
        self.compose = ["docker", "compose", "--project-name", os.environ["FLOWSTOCK_PROJECT_NAME"],
                        "--env-file", os.environ["FLOWSTOCK_ENV_FILE"],
                        "-f", os.environ["FLOWSTOCK_COMPOSE_FILE"]]
        self.container = None
        self.diagnostics = None

    def run(self, args, *, source=None, output=None, sql=None):
        # SQL errors can contain business data; diagnostics stay private.
        with contextlib.ExitStack() as stack:
            stdin = stack.enter_context(Path(source).open("rb")) if source else None
            stderr = stack.enter_context(self.diagnostics.open("ab")) if self.diagnostics else subprocess.DEVNULL
            try:
                result = subprocess.run(args, stdin=stdin, input=sql.encode() if sql is not None else None,
                                        stdout=output if output is not None else subprocess.PIPE,
                                        stderr=stderr, timeout=3600, check=True)
            except (OSError, subprocess.SubprocessError):
                raise RestoreError("command failed or outcome unknown; inspect private diagnostics") from None
        return result.stdout.decode().strip() if output is None else ""

    def pg(self, program, *args, **kwargs):
        # Pin the already healthy container; do not rediscover a different target mid-restore.
        return self.run(["docker", "exec", "-i", self.container, program, *args], **kwargs)

    def sql(self, statement, *, database="postgres"):
        return self.pg("psql", "--no-password", "--host=/var/run/postgresql", "--port=5432", "-XAt", "-v", "ON_ERROR_STOP=1",
                       "-U", self.user, "-d", database, sql=statement)

    def preflight(self):
        self.run([*self.compose, "config", "-q"])
        available = self.run([*self.compose, "config", "--services"]).splitlines()
        require(all(s in available for s in ("postgres", "flowstock", "nginx", "pgbackup")),
                "required Compose services missing")
        self.stopped_services = [s for s in self.services if s in available]
        ids = self.run([*self.compose, "ps", "-q", "postgres"]).splitlines()
        require(len(ids) == 1, "exactly one running PostgreSQL container required; no automatic startup")
        self.container = ids[0]
        health = self.run(["docker", "inspect", "--format", "{{.State.Health.Status}}", self.container])
        require(health == "healthy", "PostgreSQL must already be healthy")
        config = self.pg("sh", "-eu", "-c", 'printf "%s\\n" "$POSTGRES_DB" "$POSTGRES_USER"').splitlines()
        require(len(config) == 2 and all(re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", v) for v in config),
                "unsupported database/user name")
        self.database, self.user = config
        require(self.database not in ("postgres", "template0", "template1"), "system database forbidden")
        for tool in ("pg_dump", "pg_restore"):
            require(re.search(r"\b16\.[0-9]+\b", self.pg(tool, "--version")), "PostgreSQL 16 tools required")
        probe = json.loads(self.sql("""SELECT json_build_object(
            'version', current_setting('server_version_num')::int,
            'superuser', (SELECT rolsuper FROM pg_roles WHERE rolname=current_user),
            'system_id', (SELECT system_identifier::text FROM pg_control_system()));"""))
        require(160000 <= probe["version"] < 170000 and probe["superuser"],
                "PostgreSQL 16 and administrative role required")
        try:
            connected = self.sql("SELECT current_database()", database=self.database)
        except RestoreError:
            raise RestoreError("target database unavailable (missing, disabled or connection failure); "
                               "no automatic repair; inspect journal/safety and prepare target explicitly") from None
        require(connected == self.database, "target database mismatch")
        return {"system_id": probe["system_id"], "database": self.database,
                "project": os.environ["FLOWSTOCK_PROJECT_NAME"]}

    def validate(self, path):
        before = _archive_info(path)
        with path.open("rb") as fp:
            require(fp.read(5) == b"PGDMP", "custom-format PostgreSQL dump required")
        # --list exits after TOC; drain remaining stdin before docker exec closes.
        # Otherwise a large valid archive can cause Docker's attach stream to reset.
        for options in (("--list",), ("--exit-on-error", "--file=/dev/null")):
            self.pg("sh", "-c", '''
status=0
pg_restore --no-password --format=custom "$@" >/dev/null || status=$?
cat >/dev/null || exit 1
exit "$status"
''', "restore-validation", *options, source=path, output=subprocess.DEVNULL)
        require(_archive_info(path) == before, "archive changed during validation")
        return {"size_bytes": before[0], "sha256": before[1]}

    def stop(self):
        # common.compose_stop_existing deliberately ignores errors; restore must not.
        self.run([*self.compose, "stop", *self.stopped_services])
        self.assert_stopped()

    def assert_stopped(self):
        ids = self.run([*self.compose, "ps", "--all", "-q", *self.stopped_services]).splitlines()
        for container in ids:
            state = self.run(["docker", "inspect", "--format", "{{.State.Status}}", container])
            require(state in ("exited", "created"), "maintenance barrier failed: service is not stopped")

    def backup(self, path):
        with path.open("xb") as fp:
            os.chmod(path, 0o600)
            self.pg("pg_dump", "--no-password", "--host=/var/run/postgresql", "--port=5432",
                    "-Fc", "-U", self.user, "-d", self.database, output=fp)
            fp.flush()
            os.fsync(fp.fileno())

    def recreate(self):
        # Validated names are also quoted; do not accept libpq connection strings.
        db = self.database
        self.sql(f'ALTER DATABASE "{db}" ALLOW_CONNECTIONS false;')
        self.sql(f"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname='{db}' AND pid<>pg_backend_pid();")
        self.sql(f'DROP DATABASE "{db}";')
        self.sql(f'CREATE DATABASE "{db}" TEMPLATE template0;')

    def restore(self, path):
        self.pg("pg_restore", "--no-password", "--host=/var/run/postgresql", "--port=5432", "--format=custom", "--exit-on-error",
                "--single-transaction", "--no-owner", "--no-privileges",
                "-U", self.user, "-d", self.database, source=path, output=subprocess.DEVNULL)


class Restore:
    def __init__(self, backend, root):
        self.backend = backend
        self.root = directory(root)
        self.active = self.root / "active.json"
        self.state = None
        self.operation = None

    @contextlib.contextmanager
    def lock(self):
        fd = os.open(self.root / "lock", os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
        try:
            _file(self.root / "lock")
            try:
                fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                raise RestoreError("restore already running") from None
            yield
        finally:
            os.close(fd)

    def record(self, phase, **values):
        self.state.update(values, phase=phase, updated_at_utc=utc_now())
        # Append-only durable journal preserves the last known phase after a crash.
        number = len(list(self.operation.glob("[0-9]*.json")))
        _write_new(self.operation / f"{number:04d}.json", self.state)
        print(f"[restore] {phase}; operation={self.state['operation_id']}", flush=True)

    def previous(self, operation_id):
        active = _read(self.active)
        require(active.get("schema_version") == 1 and active.get("operation_id") == operation_id,
                "maintenance operation ID mismatch")
        require(re.fullmatch(r"[0-9a-f]{32}", operation_id), "invalid operation ID")
        operation = self.root / operation_id
        _directory(operation, create=True)
        records = sorted(operation.glob("[0-9]*.json"))
        require(bool(records), "operation journal missing; manual investigation required")
        state = _read(records[-1])
        require(state.get("schema_version") == 1 and state.get("operation_id") == operation_id,
                "unsupported or conflicting journal")
        return state

    def complete(self, operation_id):
        state = self.previous(operation_id)
        require(self.backend.preflight() == state["target"], "recovery target changed")
        self.backend.assert_stopped()
        require(state["phase"] == "RESTORED_AWAITING_VALIDATION" or
                (not state.get("destructive_started") and state.get("recovery_source") is None),
                "failed destructive restore must be recovered before completion")
        _rename_new(self.active, self.root / (operation_id + ".completed.json"))
        _fsync_dir(self.root)
        print("[restore] operator validation recorded; services remain stopped")

    def execute(self, source, *, safety_override=None, recover=None):
        previous = self.previous(recover) if recover else None
        require(previous is not None or not os.path.lexists(self.active),
                "maintenance active: inspect journal; use explicit --recover or --complete")
        operation_id = uuid.uuid4().hex
        self.operation = directory(self.root / operation_id)
        self.backend.diagnostics = self.operation / "diagnostics.log"
        self.backend.diagnostics.touch(mode=0o600, exist_ok=False)
        print(f"[restore] private diagnostics: {self.backend.diagnostics}", flush=True)
        target = self.backend.preflight()
        recovery_source = None
        if previous:
            require(target == previous["target"], "recovery target changed")
            # A failed recovery must retain the ORIGINAL good state, not its partial-DB guard.
            recovery_source = previous.get("recovery_source")
            if recovery_source is None:
                require(previous.get("safety_validated"), "no validated safety backup; investigate manually")
                recovery_source = {"path": previous["safety_path"], "info": previous["safety_info"]}
            source = Path(recovery_source["path"])
            require(self.backend.validate(source) == recovery_source["info"], "safety archive changed")
        source = Path(source)
        staged = self.operation / "input.dump"
        snapshot(source, staged)
        input_info = self.backend.validate(staged)
        safety = Path(safety_override) if safety_override else (
            Path(os.environ["FLOWSTOCK_BACKUP_OUTPUT_DIR"]) / "pre_restore" / (operation_id + ".dump"))
        safety = Path(os.path.abspath(safety))
        require(not os.path.lexists(safety), "safety output already exists; archives are never overwritten")
        require(safety != source.absolute() and safety != staged, "input/safety path collision")
        directory(safety.parent, private=False)
        self.state = {"schema_version": 1, "operation_id": operation_id, "target": target,
                      "input_info": input_info, "safety_path": str(safety), "safety_validated": False,
                      "destructive_started": False, "recovery_source": recovery_source}
        self.record("PREFLIGHT_VALIDATED")
        marker = {"schema_version": 1, "operation_id": operation_id}
        if previous:
            replacement = self.root / (operation_id + ".active")
            _write_new(replacement, marker)
            os.replace(replacement, self.active)  # no absent-barrier crash window
            _fsync_dir(self.root)
        else:
            _write_new(self.active, marker)
        try:
            self.record("STOPPING_SERVICES")
            self.backend.stop()
            self.record("CREATING_SAFETY")
            pending = safety.parent / (".restore-" + operation_id + ".partial")
            self.backend.backup(pending)
            safety_info = self.backend.validate(pending)
            _rename_new(pending, safety)
            _fsync_dir(safety.parent)
            self.record("SAFETY_VALIDATED", safety_validated=True, safety_info=safety_info)
            print(f"[restore] validated safety backup: {safety}", flush=True)
            self.backend.assert_stopped()
            require(self.backend.validate(staged) == input_info, "staged input changed")
            require(self.backend.validate(safety) == safety_info, "safety archive changed")
            self.record("RECREATING_DATABASE", destructive_started=True)
            self.backend.recreate()
            self.record("RESTORING")
            self.backend.restore(staged)
            self.record("RESTORED_AWAITING_VALIDATION")
            print("[restore] SQL restore completed; app/nginx/discovery/pgbackup remain stopped; operator validation required")
        except BaseException:
            print(f"[restore] FAILED/UNKNOWN at {self.state['phase']}; maintenance remains active; "
                  f"safety_validated={self.state['safety_validated']}; safety={safety}; "
                  f"journal={self.operation}; no automatic retry or restart", file=sys.stderr, flush=True)
            raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dump", nargs="?")
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--recover", metavar="OPERATION_ID")
    group.add_argument("--complete", metavar="OPERATION_ID")
    parser.add_argument("--confirm-validated", action="store_true")
    args = parser.parse_args()
    require(bool(args.dump) + bool(args.recover) + bool(args.complete) == 1, "select dump, --recover or --complete")
    require(args.confirm_validated == bool(args.complete), "--complete requires --confirm-validated")
    os.umask(0o077)
    restore = Restore(DockerPostgres(), Path(os.environ["FLOWSTOCK_RUNTIME_DIR"]) / "restore")
    with restore.lock():
        if args.complete:
            restore.complete(args.complete)
        else:
            restore.execute(args.dump, safety_override=os.environ.get("FLOWSTOCK_PRE_RESTORE_BACKUP_PATH_OVERRIDE"),
                            recover=args.recover)


if __name__ == "__main__":
    def interrupted(signum, frame):
        raise RestoreError("interrupted; consult durable journal before recovery")
    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    try:
        main()
    except (RestoreError, BackupError, OSError, ValueError, KeyError) as exc:
        print(f"[restore] ERROR: {exc}", file=sys.stderr)
        sys.exit(1)
