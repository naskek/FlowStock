#!/usr/bin/env python3
"""FlowStock managed PostgreSQL 16 dump primitive (not wired into production callers).

Requires Python 3 on the execution host plus PostgreSQL 16 pg_dump, pg_restore,
and (optionally) psql. Runtime packaging/wiring into pgbackup is Issue #122.
No business DB, HTTP API, scheduler, retention deletion or remote I/O.
"""
from __future__ import annotations

import argparse
import contextlib
import datetime as dt
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import time
import uuid

VERSION = 1
ORIGINS = {"scheduled", "manual", "pre_deploy", "pre_restore"}
PREFIX = re.compile(r"^FlowStock_[0-9]{8}_[0-9]{6}[.]dump$")
UUID_NAME = re.compile(r"^[0-9a-f]{32}$")
CHUNK = 1024 * 1024


class BackupError(Exception):
    pass


def utc_now():
    return dt.datetime.now(dt.timezone.utc).isoformat().replace("+00:00", "Z")


def _directory(path: Path, create: bool = False) -> None:
    if create:
        try:
            path.mkdir(mode=0o700)
        except FileExistsError:
            pass
    item = os.lstat(path)
    if not stat.S_ISDIR(item.st_mode) or item.st_uid != os.geteuid():
        raise BackupError("STORAGE_UNSAFE: directory ownership/type")
    if create and stat.S_IMODE(item.st_mode) != 0o700:
        raise BackupError("STORAGE_UNSAFE: managed directory mode")


def _file(path: Path, *, strict: bool = True) -> os.stat_result:
    item = os.lstat(path)
    if not stat.S_ISREG(item.st_mode) or item.st_nlink != 1:
        raise BackupError("STORAGE_UNSAFE: file type/link count")
    if strict and (item.st_uid != os.geteuid() or stat.S_IMODE(item.st_mode) != 0o600):
        raise BackupError("STORAGE_UNSAFE: managed file ownership/mode")
    return item


def _read(path: Path):
    _file(path)
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
    with os.fdopen(os.open(path, flags), "rb") as fp:
        return json.load(fp)


def _fsync_dir(path: Path):
    fd = os.open(path, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0) | getattr(os, "O_NOFOLLOW", 0))
    try:
        os.fsync(fd)
    finally:
        os.close(fd)


def _write_new(path: Path, value: dict):
    data = (json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n").encode()
    temp = path.parent / (".tmp-" + uuid.uuid4().hex)
    flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
    fd = os.open(temp, flags, 0o600)
    try:
        with os.fdopen(fd, "wb") as fp:
            fp.write(data)
            fp.flush()
            os.fsync(fp.fileno())
        os.link(temp, path, follow_symlinks=False)  # publish without overwrite
        _fsync_dir(path.parent)
    finally:
        temp.unlink(missing_ok=True)


def _remove_own(path: Path):
    _file(path)
    path.unlink()
    _fsync_dir(path.parent)


def _archive_info(path: Path) -> tuple[int, str]:
    before = _file(path)
    digest = hashlib.sha256()
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
    with os.fdopen(os.open(path, flags), "rb") as fp:
        if os.fstat(fp.fileno()).st_ino != before.st_ino:
            raise BackupError("STORAGE_UNSAFE: archive changed")
        while True:
            block = fp.read(CHUNK)
            if not block:
                break
            digest.update(block)
    after = _file(path)
    if before.st_ino != after.st_ino or before.st_size != after.st_size or before.st_mtime_ns != after.st_mtime_ns:
        raise BackupError("STORAGE_UNSAFE: archive changed during hashing")
    if before.st_size <= 0:
        raise BackupError("DUMP_INVALID: empty archive")
    return before.st_size, digest.hexdigest()


def _run(args: list[str], timeout: int = 3600):
    try:
        subprocess.run(args, check=True, stdout=subprocess.DEVNULL,
                       stderr=subprocess.DEVNULL, timeout=timeout)
    except (OSError, subprocess.SubprocessError) as exc:
        if isinstance(exc, subprocess.TimeoutExpired):
            raise BackupError("DUMP_FAILED: command timed out") from None
        raise BackupError("DUMP_INVALID: PostgreSQL command failed") from None


def _validate(path: Path):
    _archive_info(path)
    _run(["pg_restore", "--list", str(path)])
    # Unlike --list, generating SQL reads data blocks and catches truncated payloads.
    _run(["pg_restore", "--file=/dev/null", str(path)])
    return _archive_info(path)


def _migrations():
    try:
        out = subprocess.run(
            ["psql", "-X", "-A", "-t", "-v", "ON_ERROR_STOP=1",
             "-c", "SELECT version, filename FROM schema_migrations ORDER BY version"],
            check=True, capture_output=True, timeout=15)
        return hashlib.sha256(out.stdout).hexdigest(), "KNOWN"
    except (OSError, subprocess.SubprocessError):
        return None, "UNKNOWN"


class Store:
    def __init__(self, root: str):
        root_path = Path(root)
        if not root_path.is_absolute() or ".." in root_path.parts:
            raise BackupError("INVALID_PATH: absolute backup root required")
        # Inspect every path component, not Path.resolve() (which follows symlinks).
        current = Path("/")
        for part in root_path.parts[1:]:
            current = current / part
            if stat.S_ISLNK(os.lstat(current).st_mode):
                raise BackupError("STORAGE_UNSAFE: symlink in root")
        _directory(root_path)
        self.root = root_path
        self.managed = root_path / "managed"
        self.archives = self.managed / "archives"
        self.staging = self.managed / "staging"
        self.catalog = self.managed / "catalog"
        self.jobs = self.managed / "jobs"
        self.maintenance = self.managed / "maintenance"
        for p in [self.managed, self.archives, self.staging,
                  self.catalog, self.jobs, self.maintenance]:
            _directory(p, create=True)

    @contextlib.contextmanager
    def lock(self, timeout: float):
        if timeout <= 0 or timeout > 300:
            raise BackupError("INVALID_REQUEST: lock timeout")
        path = self.maintenance / "lock"
        fd = os.open(path, os.O_RDWR | os.O_CREAT | getattr(os, "O_NOFOLLOW", 0), 0o600)
        try:
            _file(path)
            deadline = time.monotonic() + timeout
            while True:
                try:
                    fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
                    break
                except BlockingIOError:
                    if time.monotonic() >= deadline:
                        raise BackupError("BACKUP_BUSY: shared lock timed out")
                    time.sleep(0.1)
            yield
        finally:
            os.close(fd)  # flock released by kernel on kill/recreate

    def paused(self):
        p = self.maintenance / "pause.json"
        if p.exists() or p.is_symlink():
            return _read(p)
        return None

    def pause(self, reason: str):
        if self.paused():
            raise BackupError("MAINTENANCE_PAUSED: already paused")
        _write_new(self.maintenance / "pause.json",
                   {"schema_version": VERSION, "paused_at_utc": utc_now(),
                    "reason": reason})

    def resume(self):
        if not self.paused():
            raise BackupError("INVALID_REQUEST: not paused")
        _remove_own(self.maintenance / "pause.json")

    def _space(self, estimate: int | None):
        completed = sorted(self.catalog.glob("*.json"), key=lambda p: p.name)
        sizes = []
        for path in completed:
            record = _read(path)
            if record.get("local_state") == "LOCAL_VALID":
                sizes.append(int(record["size_bytes"]))
        expected = max(sizes[-3:]) if sizes else estimate
        if expected is None or expected < 1:
            raise BackupError("STORAGE_LOW: first backup requires --expected-bytes")
        usage = shutil.disk_usage(self.root)
        if usage.free < 2 * expected or usage.free < usage.total // 10:
            raise BackupError("STORAGE_LOW: insufficient reserved free space")

    def _pending(self, origin, legacy_source=None):
        ident = uuid.uuid4().hex
        item = {"schema_version": VERSION, "backup_id": ident,
                "origin": origin, "created_at_utc": utc_now(),
                "legacy_source": legacy_source}
        _write_new(self.jobs / (ident + ".json"), item)
        return item

    def _publish(self, item, stage: Path):
        ident = item["backup_id"]
        target = self.archives / (ident + ".dump")
        record_path = self.catalog / (ident + ".json")
        if target.exists() or target.is_symlink():
            # Recovery may discover final archive published before manifest.
            if stage.exists():
                a, b = os.lstat(target), os.lstat(stage)
                if a.st_ino != b.st_ino:
                    raise BackupError("STORAGE_UNSAFE: conflicting archive identity")
                if a.st_nlink != 2:
                    raise BackupError("STORAGE_UNSAFE: unexpected staging hardlink")
                stage.unlink()
                _fsync_dir(self.staging)
        else:
            _file(stage)
            os.link(stage, target, follow_symlinks=False)
            _fsync_dir(self.archives)
            stage.unlink()
            _fsync_dir(self.staging)

        size, digest = _validate(target)
        if record_path.exists() or record_path.is_symlink():
            saved = _read(record_path)
            if saved.get("sha256") != digest or saved.get("size_bytes") != size:
                raise BackupError("STORAGE_UNSAFE: catalog/archive mismatch")
        else:
            pg_fingerprint, pg_state = _migrations()
            record = {
                "schema_version": VERSION, "backup_id": ident,
                "origin": item["origin"], "created_at_utc": item["created_at_utc"],
                "completed_at_utc": utc_now(), "archive_key": ident,
                "size_bytes": size, "sha256": digest,
                "pg_major": 16, "schema_migrations_fingerprint": pg_fingerprint,
                "schema_migrations_state": pg_state,
                "structural_validated": True, "restore_test_at_utc": None,
                "local_state": "LOCAL_VALID", "remote_state": "DISABLED",
                "destination_revision": None,
                "legacy_source": item.get("legacy_source")}
            _write_new(record_path, record)
        _remove_own(self.jobs / (ident + ".json"))
        return _read(record_path)

    def create(self, origin, estimate, during_maintenance=False):
        if origin not in ORIGINS:
            raise BackupError("INVALID_REQUEST: origin")
        if self.paused() and not (origin == "pre_restore" and during_maintenance):
            raise BackupError("MAINTENANCE_PAUSED: dump disabled")
        self._space(estimate)
        item = self._pending(origin)
        stage = self.staging / (item["backup_id"] + ".part")
        try:
            _run(["pg_dump", "--format=custom", "--no-owner", "--no-acl", "-f", str(stage)])
            # pg_dump inherits PG* environment. Never pass credentials on argv.
            os.chmod(stage, 0o600)
            _validate(stage)
            return self._publish(item, stage)
        except Exception:
            # Do not delete staging or overwrite catalog; recover inspects them.
            raise

    def import_legacy(self, name: str, estimate: int | None):
        if not PREFIX.fullmatch(name):
            raise BackupError("INVALID_PATH: only legacy dump basename accepted")
        source = self.root / name
        _archive_info(source)  # reject symlinks/hardlinks, preserve source
        self._space(estimate)
        item = self._pending("legacy_import", name)
        stage = self.staging / (item["backup_id"] + ".part")
        flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
        with os.fdopen(os.open(stage, flags, 0o600), "wb") as dest:
            with open(source, "rb") as src:
                shutil.copyfileobj(src, dest, CHUNK)
            dest.flush()
            os.fsync(dest.fileno())
        _validate(stage)
        return self._publish(item, stage)

    def recover(self):
        fixed, incomplete = [], []
        for path in sorted(self.jobs.glob("*.json")):
            item = _read(path)
            ident = item.get("backup_id", "")
            if not UUID_NAME.fullmatch(ident) or path.name != ident + ".json":
                raise BackupError("STORAGE_UNSAFE: invalid pending job")
            stage = self.staging / (ident + ".part")
            target = self.archives / (ident + ".dump")
            if stage.exists() or stage.is_symlink() or target.exists() or target.is_symlink():
                try:
                    fixed.append(self._publish(item, stage)["backup_id"])
                except BackupError:
                    incomplete.append(ident)
            else:
                incomplete.append(ident)
        return {"recovered": fixed, "operator_review_required": incomplete}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", required=True, help="existing absolute backup root")
    parser.add_argument("--lock-timeout", type=float, default=10)
    sub = parser.add_subparsers(dest="operation", required=True)
    create = sub.add_parser("create")
    create.add_argument("--origin", required=True, choices=sorted(ORIGINS))
    create.add_argument("--expected-bytes", type=int)
    create.add_argument("--during-maintenance", action="store_true")
    imported = sub.add_parser("import-legacy")
    imported.add_argument("--name", required=True)
    imported.add_argument("--expected-bytes", type=int)
    sub.add_parser("recover")
    paused = sub.add_parser("pause")
    paused.add_argument("--reason", required=True)
    resumed = sub.add_parser("resume")
    resumed.add_argument("--operator-confirmed", action="store_true", required=True)
    sub.add_parser("status")
    args = parser.parse_args()
    os.umask(0o077)
    try:
        store = Store(args.root)
        with store.lock(args.lock_timeout):
            if args.operation == "create":
                value = store.create(args.origin, args.expected_bytes,
                                     args.during_maintenance)
            elif args.operation == "import-legacy":
                if store.paused():
                    raise BackupError("MAINTENANCE_PAUSED: import disabled")
                value = store.import_legacy(args.name, args.expected_bytes)
            elif args.operation == "recover":
                value = store.recover()
            elif args.operation == "pause":
                store.pause(args.reason)
                value = {"paused": True}
            elif args.operation == "resume":
                store.resume()
                value = {"paused": False}
            else:
                value = {"paused": bool(store.paused()),
                         "archives": len(list(store.catalog.glob("*.json"))),
                         "pending": len(list(store.jobs.glob("*.json")))}
        print(json.dumps(value, sort_keys=True))
        return 0
    except (BackupError, OSError, ValueError, json.JSONDecodeError) as exc:
        code = str(exc).split(":", 1)[0] if isinstance(exc, BackupError) else "BACKUP_FAILED"
        print(json.dumps({"ok": False, "error_code": code}), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
