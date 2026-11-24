#!/usr/bin/env python3
"""Durable local pgbackup scheduler. Operator CLI; no HTTP, remote or DB writes."""
from __future__ import annotations

import argparse
import datetime as dt
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import sys
import time
import uuid

import managed_backup as core

Error = core.BackupError
UTC = dt.timezone.utc
RETRIES = (300, 900, 3600)


def require(condition, code="INVALID_CONFIG"):
    if not condition:
        raise Error(code)


def stamp(seconds):
    return dt.datetime.fromtimestamp(seconds, UTC).isoformat().replace("+00:00", "Z")


def seconds(value):
    require(isinstance(value, str) and value.endswith("Z"), "STATE_INVALID")
    return dt.datetime.fromisoformat(value[:-1] + "+00:00").timestamp()


def canonical(value):
    return json.dumps(value, sort_keys=True, separators=(",", ":")).encode()


def replace(path, value):
    """Atomic durable replacement of our own regular private JSON snapshot."""
    if path.exists() or path.is_symlink():
        core._file(path)
    temp = path.parent / (".update-" + uuid.uuid4().hex)
    core._write_new(temp, value)
    os.replace(temp, path)
    core._fsync_dir(path.parent)


def configuration(value):
    require(isinstance(value, dict) and set(value) == {"schema_version", "scheduler", "local"})
    require(type(value["schema_version"]) is int and value["schema_version"] == 1,
            "UNSUPPORTED_SCHEMA")
    scheduler, local = value["scheduler"], value["local"]
    require(isinstance(scheduler, dict) and set(scheduler) == {"enabled", "interval_hours"})
    require(type(scheduler["enabled"]) is bool and type(scheduler["interval_hours"]) is int
            and 6 <= scheduler["interval_hours"] <= 168)
    require(isinstance(local, dict) and set(local) == {"storage_root_alias", "keep_count", "keep_days"})
    require(local["storage_root_alias"] == "managed_pgbackups")
    require(type(local["keep_count"]) is int and 7 <= local["keep_count"] <= 90)
    require(type(local["keep_days"]) is int and 7 <= local["keep_days"] <= 365)
    return value


def default_config():
    # A new scheduler must be enabled explicitly after the operator cutover.
    return {"schema_version": 1, "scheduler": {"enabled": False, "interval_hours": 24},
            "local": {"storage_root_alias": "managed_pgbackups", "keep_count": 30, "keep_days": 30}}


class Scheduler:
    def __init__(self, store, *, clock=time.time, expected_bytes=None):
        self.store, self.clock, self.expected_bytes = store, clock, expected_bytes
        self.folder = store.managed / "scheduler"
        self.config = store.managed / "config"
        self.revisions = self.config / "revisions"
        self.prunes = store.managed / "retention"
        for folder in (self.folder, self.config, self.revisions, self.prunes):
            core._directory(folder, create=True)
        self.checked = {}

    def bootstrap(self):
        applied = self.config / "applied.json"
        if not applied.exists() and not applied.is_symlink():
            # Missing metadata after a previous cutover is not a new installation.
            require(not any(self.revisions.iterdir()) and not (self.config / "requested.json").exists(),
                    "CONFIG_MISSING")
            initial = {"revision": 0, "config": default_config()}
            core._write_new(self.revisions / "0.json", initial)
            core._write_new(applied, initial)
        return self.read_config(applied)

    def read_config(self, path):
        value = core._read(path)
        require(isinstance(value, dict) and set(value) == {"revision", "config"}, "INVALID_CONFIG")
        require(type(value["revision"]) is int and value["revision"] >= 0, "INVALID_CONFIG")
        configuration(value["config"])
        require(core._read(self.revisions / (str(value["revision"]) + ".json")) == value,
                "CONFIG_CONFLICT")
        return value

    def requested(self, applied):
        path = self.config / "requested.json"
        return self.read_config(path) if path.exists() or path.is_symlink() else applied

    def configure(self, value, expected_revision, preview_token=None):
        """Caller holds the shared lock. Validation precedes publication."""
        configuration(value)
        applied = self.bootstrap()
        requested = self.requested(applied)
        require(expected_revision == requested["revision"], "CONFIG_CONFLICT")
        old, new = requested["config"]["local"], value["local"]
        if new["keep_count"] < old["keep_count"] or new["keep_days"] < old["keep_days"]:
            preview = self.preview(value, requested["revision"])
            require(preview_token == preview["confirmation_token"], "RETENTION_CONFIRMATION_REQUIRED")
        revision = requested["revision"] + 1
        # A crash after the immutable file but before the pointer leaves an orphan
        # revision, never an applied change. Allocate the next unused number.
        while (self.revisions / (str(revision) + ".json")).exists():
            revision += 1
        result = {"revision": revision, "config": value}
        core._write_new(self.revisions / (str(revision) + ".json"), result)
        replace(self.config / "requested.json", result)
        return {"requested_revision": revision, "applied_revision": applied["revision"], "apply_state": "PENDING"}

    def records(self):
        records = []
        for path in sorted(self.store.catalog.glob("*.json")):
            item = core._read(path)
            require(isinstance(item, dict), "CATALOG_INVALID")
            ident = item.get("backup_id", "")
            require(isinstance(ident, str) and core.UUID_NAME.fullmatch(ident)
                    and path.name == ident + ".json" and item.get("archive_key") == ident,
                    "CATALOG_INVALID")
            require(type(item.get("schema_version")) is int and item["schema_version"] == 1, "UNSUPPORTED_SCHEMA")
            require(item.get("local_state") == "LOCAL_VALID" and item.get("structural_validated") is True
                    and item.get("pg_major") == 16, "CATALOG_INVALID")
            require(type(item.get("size_bytes")) is int and item["size_bytes"] > 0
                    and isinstance(item.get("sha256"), str)
                    and re.fullmatch(r"[0-9a-f]{64}", item["sha256"]), "CATALOG_INVALID")
            require(item.get("origin") in core.ORIGINS | {"legacy_import"}, "CATALOG_INVALID")
            require(item.get("remote_state") in {"DISABLED", "PENDING", "UPLOADING", "VERIFYING", "VERIFIED",
                                               "RETRY_WAIT", "REMOTE_FAILED"}, "CATALOG_INVALID")
            require(isinstance(item.get("pins", []), list)
                    and all(isinstance(pin, str) and pin for pin in item.get("pins", [])), "CATALOG_INVALID")
            require(item.get("local_presence", "PRESENT") in {"PRESENT", "PRUNED"}, "CATALOG_INVALID")
            require(seconds(item["created_at_utc"]) <= seconds(item["completed_at_utc"]), "CATALOG_INVALID")
            archive = self.store.archives / (ident + ".dump")
            if item.get("local_presence") == "PRUNED":
                journal = core._read(self.prunes / (ident + ".json"))
                require(journal.get("sha256") == item["sha256"] and journal.get("phase") == "PRUNED"
                        and not archive.exists() and not archive.is_symlink(), "RETENTION_UNKNOWN_OUTCOME")
            else:
                core._file(archive)
                require(archive.stat().st_size == item["size_bytes"], "CATALOG_INVALID")
            records.append(item)
        return records

    def verify(self, item, *, fresh=False):
        path = self.store.archives / (item["backup_id"] + ".dump")
        info = core._file(path)
        identity = (info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns, item["sha256"])
        if fresh or self.checked.get(item["backup_id"]) != identity:
            require(core._validate(path) == (item["size_bytes"], item["sha256"]), "CATALOG_INVALID")
            self.checked[item["backup_id"]] = identity

    @staticmethod
    def present(records):
        return sorted((r for r in records if r.get("local_presence", "PRESENT") == "PRESENT"),
                      key=lambda r: (seconds(r["completed_at_utc"]), r["backup_id"]), reverse=True)

    def recover_jobs(self):
        result = self.store.recover()
        for ident in result["operator_review_required"]:
            # A ready marker with an uncertain publication cannot be retried.
            require(not (self.store.jobs / (ident + ".ready")).exists(), "UNKNOWN_OUTCOME")
            path = self.store.jobs / (ident + ".json")
            item = core._read(path)
            result_path = self.store.jobs / (ident + ".result")
            terminal = {
                "schema_version": 1, "backup_id": ident, "job_state": "LOCAL_FAILED",
                "error_code": "INTERRUPTED", "finished_at_utc": stamp(self.clock())}
            if result_path.exists() or result_path.is_symlink():
                saved = core._read(result_path)
                require(saved.get("backup_id") == ident and saved.get("job_state") == "LOCAL_FAILED",
                        "UNKNOWN_OUTCOME")
            else:
                core._write_new(result_path, terminal)
            # Preserve the original job and unvalidated partial for operator review.
            core._rename_new(path, self.store.jobs / (ident + ".failed"))
            core._fsync_dir(self.store.jobs)
        return result

    def recover_prunes(self):
        for path in self.prunes.glob("*.json"):
            job = core._read(path)
            ident = job.get("backup_id", "")
            require(isinstance(ident, str) and core.UUID_NAME.fullmatch(ident) and path.name == ident + ".json"
                    and job.get("schema_version") == 1 and job.get("phase") in {"PRUNING", "PRUNED"},
                    "RETENTION_UNKNOWN_OUTCOME")
            record_path = self.store.catalog / (ident + ".json")
            record = core._read(record_path)
            require(record.get("sha256") == job.get("sha256"), "RETENTION_UNKNOWN_OUTCOME")
            archive = self.store.archives / (ident + ".dump")
            if job["phase"] == "PRUNING":
                if archive.exists() or archive.is_symlink():
                    # Before unlink: leave it intact. A later fresh plan rechecks
                    # pins, barrier, hash and keeper before reusing this intent.
                    self.verify(record, fresh=True)
                    continue
                require(not self.store.paused(), "MAINTENANCE_PAUSED")
                record = record | {"local_presence": "PRUNED", "pruned_at_utc": job["at_utc"]}
                replace(record_path, record)
                replace(path, job | {"phase": "PRUNED"})

    def plan(self, records, config):
        present = self.present(records)
        now = self.clock()
        latest_scheduled = next((r["backup_id"] for r in present if r["origin"] == "scheduled"), None)
        candidates, protected = [], []
        for index, item in enumerate(present):
            reason = []
            if index >= config["local"]["keep_count"]:
                reason.append("COUNT")
            if now - seconds(item["created_at_utc"]) > config["local"]["keep_days"] * 86400:
                reason.append("AGE")
            if not reason:
                continue
            # Safety and imported provenance remain pinned conservatively. Future
            # remote/pin writers must use the very same store lock.
            held = (index == 0 or item["backup_id"] == latest_scheduled or item.get("pins")
                    or item["origin"] in {"pre_deploy", "pre_restore", "legacy_import"}
                    or item["remote_state"] not in {"DISABLED", "VERIFIED"})
            (protected if held else candidates).append({"backup_id": item["backup_id"], "reason": reason})
        return candidates, protected

    def preview(self, config, revision):
        configuration(config)
        require(not self.store.paused(), "MAINTENANCE_PAUSED")
        require(not any(self.store.jobs.glob("*.json")), "BACKUP_BUSY")
        records = self.records()
        candidates, protected = self.plan(records, config)
        for item in self.present(records):
            self.verify(item)
        # Bind the operator confirmation to revision, policy and complete current
        # inventory. A different candidate set or pin/hash invalidates the token.
        token = hashlib.sha256(canonical({"revision": revision, "config": config, "records": records,
                                         "candidates": candidates, "protected": protected})).hexdigest()
        return {"candidates": candidates, "protected": protected, "confirmation_token": token}

    def prune(self, records, config):
        require(not self.store.paused(), "MAINTENANCE_PAUSED")
        require(not any(self.store.jobs.glob("*.json")), "BACKUP_BUSY")
        candidates, protected = self.plan(records, config)
        if not candidates:
            return {"pruned": [], "protected_over_limit": protected}
        keeper = self.present(records)[0] if self.present(records) else None
        # Preflight every candidate and the keeper before the first deletion.
        selected = {x["backup_id"] for x in candidates}
        for item in records:
            if item["backup_id"] in selected or item is keeper:
                self.verify(item, fresh=True)
        removed = []
        for item in records:
            ident = item["backup_id"]
            if ident not in selected:
                continue
            require(not self.store.paused(), "MAINTENANCE_PAUSED")
            # Re-read protected metadata just before unlink; no HTTP/SFTP writer
            # may change it without holding the shared lock.
            require(core._read(self.store.catalog / (ident + ".json")) == item, "CATALOG_INVALID")
            self.verify(item, fresh=True)
            self.verify(keeper, fresh=True)
            job = {"schema_version": 1, "backup_id": ident, "sha256": item["sha256"],
                   "phase": "PRUNING", "at_utc": stamp(self.clock()),
                   "reason": next(c["reason"] for c in candidates if c["backup_id"] == ident)}
            replace(self.prunes / (ident + ".json"), job)
            require(not self.store.paused(), "MAINTENANCE_PAUSED")
            self.verify(item, fresh=True)
            self.verify(keeper, fresh=True)
            core._remove_own(self.store.archives / (ident + ".dump"))
            replace(self.store.catalog / (ident + ".json"),
                    item | {"local_presence": "PRUNED", "pruned_at_utc": job["at_utc"]})
            replace(self.prunes / (ident + ".json"), job | {"phase": "PRUNED"})
            removed.append(ident)
        return {"pruned": removed, "protected_over_limit": protected}

    def load_state(self, records):
        path = self.folder / "state.json"
        scheduled = [r for r in self.present(records) if r["origin"] == "scheduled"]
        latest = scheduled[0] if scheduled else None
        # UUID ordering is not evidence of which success happened last.
        if latest:
            require(sum(seconds(r["completed_at_utc"]) == seconds(latest["completed_at_utc"])
                        for r in scheduled) == 1, "CATALOG_AMBIGUOUS")
            self.verify(latest)
        reset = False
        try:
            value = core._read(path)
            require(isinstance(value, dict) and "schema_version" in value, "STATE_INVALID")
            require(type(value["schema_version"]) is int and value["schema_version"] == 1, "UNSUPPORTED_SCHEMA")
            require({"failures", "job_state", "last_scheduled_success_at_utc", "last_scheduled_backup_id",
                     "last_attempt_at_utc", "next_retry_at_utc", "observed_at_utc", "error_code"} <= value.keys(),
                    "STATE_INVALID")
            require(type(value.get("failures")) is int and value["failures"] >= 0, "STATE_INVALID")
            require(value.get("job_state") in {"IDLE", "RUNNING", "RETRY_WAIT"}, "STATE_INVALID")
            ident = value["last_scheduled_backup_id"]
            require(ident is None or isinstance(ident, str) and core.UUID_NAME.fullmatch(ident), "STATE_INVALID")
            require((ident is None) == (value["last_scheduled_success_at_utc"] is None), "STATE_INVALID")
            require(value["observed_at_utc"] is not None, "STATE_INVALID")
            require(value["error_code"] is None or isinstance(value["error_code"], str)
                    and re.fullmatch(r"[A-Z_]+", value["error_code"]), "STATE_INVALID")
            for key in ("last_scheduled_success_at_utc", "last_attempt_at_utc", "next_retry_at_utc", "observed_at_utc"):
                if value.get(key) is not None:
                    seconds(value[key])
            previous = value["last_scheduled_success_at_utc"]
            if previous is not None:
                require(latest is not None and seconds(previous) <= seconds(latest["completed_at_utc"])
                        and any(r["origin"] == "scheduled" and r["backup_id"] == ident
                                and r["completed_at_utc"] == previous for r in records), "STATE_INVALID")
        except Error as exc:
            if str(exc) != "STATE_INVALID":
                raise
            reset = True
        except (FileNotFoundError, ValueError, KeyError, TypeError):
            reset = True
        if reset:
            # Catalog/verification errors must not be mistaken for corrupt state.
            # Reconstruct only after all surviving evidence has been verified.
            for record in self.present(records):
                self.verify(record, fresh=True)
            if path.exists() or path.is_symlink():
                core._file(path)  # never follow or replace a symlink/cross-owner path
                saved = self.folder / ("state-" + uuid.uuid4().hex + ".invalid")
                with path.open("rb") as source, saved.open("xb") as dest:
                    os.chmod(saved, 0o600)
                    shutil.copyfileobj(source, dest)
                    dest.flush()
                    os.fsync(dest.fileno())
                core._fsync_dir(self.folder)
            value = {"schema_version": 1, "failures": 0, "job_state": "IDLE", "last_attempt_at_utc": None,
                     "last_scheduled_success_at_utc": None, "last_scheduled_backup_id": None,
                     "next_retry_at_utc": None, "observed_at_utc": stamp(self.clock()), "error_code": None}
        if latest:
            previous = value["last_scheduled_success_at_utc"]
            if previous is None or seconds(latest["completed_at_utc"]) > seconds(previous):
                value.update(last_scheduled_success_at_utc=latest["completed_at_utc"],
                             last_scheduled_backup_id=latest["backup_id"], failures=0,
                             next_retry_at_utc=None, job_state="IDLE", error_code=None)
            else:
                require(value["last_scheduled_backup_id"] == latest["backup_id"]
                        and previous == latest["completed_at_utc"], "STATE_INVALID")
        else:
            require(value["last_scheduled_success_at_utc"] is None, "STATE_INVALID")
        if value["job_state"] == "RUNNING":
            self.failed(value, "INTERRUPTED")
        if reset and latest is None:
            self.failed(value, "OPERATOR_ACTION_REQUIRED")
        if self.clock() < seconds(value["observed_at_utc"]):
            value["clock_warning"] = "CLOCK_ROLLBACK"
            if value["next_retry_at_utc"]:
                value["next_retry_at_utc"] = stamp(self.clock() + RETRIES[min(max(value["failures"] - 1, 0), 2)])
        return value

    def failed(self, state, code):
        state["failures"] += 1
        state.update(job_state="RETRY_WAIT", error_code=code,
                     next_retry_at_utc=stamp(self.clock() + RETRIES[min(state["failures"] - 1, 2)]))

    def save_state(self, state):
        state["observed_at_utc"] = stamp(self.clock())
        replace(self.folder / "state.json", state)

    def tick(self):
        """One bounded decision, called under the shared lock, including dump."""
        applied = self.bootstrap()
        try:
            requested = self.requested(applied)
            require(requested["revision"] >= applied["revision"], "CONFIG_CONFLICT")
            if requested != applied:
                replace(self.config / "applied.json", requested)
                applied = requested
            config = applied["config"]
            if self.store.paused():
                return self.observe(None, applied, "PAUSED", "MAINTENANCE_PAUSED")
            self.recover_jobs()
            self.recover_prunes()
            records = self.records()
            state = self.load_state(records)
            self.save_state(state)
            if not config["scheduler"]["enabled"]:
                return self.observe(state, applied, "DISABLED")
            success = state["last_scheduled_success_at_utc"]
            deadline = seconds(success) + config["scheduler"]["interval_hours"] * 3600 if success else self.clock()
            state["next_run_at_utc"] = stamp(deadline)
            retry = state["next_retry_at_utc"]
            if self.clock() >= deadline and (retry is None or self.clock() >= seconds(retry)):
                state.update(job_state="RUNNING", last_attempt_at_utc=stamp(self.clock()))
                self.save_state(state)  # crash never masquerades as success
                try:
                    item = self.store.create("scheduled", self.expected_bytes)
                except (Error, OSError, ValueError) as exc:
                    code = str(exc).split(":", 1)[0] if isinstance(exc, Error) else "DUMP_FAILED"
                    self.failed(state, code)
                    self.save_state(state)
                    self.recover_jobs()
                    return self.observe(state, applied, "DEGRADED", code)
                state.update(last_scheduled_success_at_utc=item["completed_at_utc"],
                             last_scheduled_backup_id=item["backup_id"], job_state="IDLE", failures=0,
                             next_retry_at_utc=None, error_code=None,
                             next_run_at_utc=stamp(seconds(item["completed_at_utc"]) + config["scheduler"]["interval_hours"] * 3600))
                self.save_state(state)
                records = self.records()
                print(json.dumps(item, sort_keys=True), flush=True)
            # Storage pressure never triggers an emergency cleanup of backups.
            self.store._space(self.expected_bytes)
            retention = self.prune(records, config)
            self.save_state(state)
            code = state.get("error_code") or ("RETENTION_PROTECTED" if retention["protected_over_limit"] else None)
            return self.observe(state, applied, "DEGRADED" if code else "HEALTHY", code, retention)
        except (Error, OSError, ValueError, KeyError, TypeError) as exc:
            code = (str(exc).split(":", 1)[0] if isinstance(exc, Error) else
                    "STORAGE_IO_ERROR" if isinstance(exc, OSError) else "STATE_INVALID")
            return self.observe(None, applied, "DEGRADED", code)

    def observe(self, state, applied, health, error=None, retention=None):
        usage = shutil.disk_usage(self.store.root)
        result = {"schema_version": 1, "observed_at_utc": stamp(self.clock()), "health": health,
                  "error_code": error, "applied_revision": applied["revision"],
                  "effective_config": applied["config"], "schedule": state,
                  "retention": retention, "free_bytes": usage.free, "total_bytes": usage.total,
                  "storage_roots": [{"alias": "managed_pgbackups", "available": True}],
                  "structural_validation_is_restore_proof": False}
        try:
            requested = self.requested(applied)
            result.update(requested_revision=requested["revision"],
                          apply_state="APPLIED" if requested == applied else "REJECTED")
        except (Error, OSError, ValueError, KeyError, TypeError):
            result.update(requested_revision=None, apply_state="REJECTED")
        if state and state.get("last_scheduled_backup_id"):
            item = core._read(self.store.catalog / (state["last_scheduled_backup_id"] + ".json"))
            result["last_scheduled_archive"] = {k: item[k] for k in (
                "backup_id", "completed_at_utc", "size_bytes", "sha256", "structural_validated", "restore_test_at_utc")}
        replace(self.folder / "observation.json", result)
        return result


def status(root):
    # Pure read: do not construct Store (which provisions directories) or acquire
    # a writable lock. Snapshots are atomic; observation age is explicit.
    path = Path(root)
    require(path.is_absolute() and ".." not in path.parts, "INVALID_PATH")
    current = Path("/")
    for part in (path / "managed/scheduler").parts[1:]:
        current /= part
        require(not current.is_symlink(), "STORAGE_UNSAFE")
    folder = path / "managed" / "scheduler"
    core._directory(folder)
    result = core._read(folder / "observation.json")
    require(result.get("schema_version") == 1, "UNSUPPORTED_SCHEMA")
    applied_path, requested_path = path / "managed/config/applied.json", path / "managed/config/requested.json"
    for directory in (applied_path.parent, folder):
        core._directory(directory)
    applied = core._read(applied_path)
    requested = core._read(requested_path) if requested_path.exists() else applied
    configuration(applied["config"])
    configuration(requested["config"])
    result.update(applied_revision=applied["revision"], requested_revision=requested["revision"],
                  apply_state="APPLIED" if requested == applied else "PENDING",
                  effective_config=applied["config"],
                  observation_age_seconds=max(0, time.time() - seconds(result["observed_at_utc"])))
    if result["observation_age_seconds"] > 90:
        result.update(health="DEGRADED", error_code="WORKER_UNAVAILABLE")
    pause_path = path / "managed/maintenance/pause.json"
    core._directory(pause_path.parent)
    if pause_path.exists() or pause_path.is_symlink():
        core._read(pause_path)
        result.update(health="PAUSED", error_code="MAINTENANCE_PAUSED")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", required=True)
    parser.add_argument("--expected-bytes", type=int)
    parser.add_argument("--lock-timeout", type=float, default=10)
    sub = parser.add_subparsers(dest="operation", required=True)
    sub.add_parser("run")
    sub.add_parser("tick")
    sub.add_parser("status")
    configure = sub.add_parser("configure")
    configure.add_argument("--file", required=True)
    configure.add_argument("--expected-revision", type=int, required=True)
    configure.add_argument("--confirm-retention-preview")
    preview = sub.add_parser("preview-config")
    preview.add_argument("--file", required=True)
    args = parser.parse_args()
    os.umask(0o077)
    try:
        if args.operation == "status":
            print(json.dumps(status(args.root), sort_keys=True))
            return 0
        store = core.Store(args.root)
        scheduler = Scheduler(store, expected_bytes=args.expected_bytes)
        while True:
            try:
                with store.lock(args.lock_timeout):
                    if args.operation in {"tick", "run"}:
                        result = scheduler.tick()
                    else:
                        config = configuration(core._read(Path(args.file)))
                        if args.operation == "configure":
                            result = scheduler.configure(config, args.expected_revision, args.confirm_retention_preview)
                        else:
                            applied = scheduler.bootstrap()
                            result = scheduler.preview(config, scheduler.requested(applied)["revision"])
                print(json.dumps(result, sort_keys=True), flush=True)
            except (Error, OSError, ValueError, KeyError, TypeError) as exc:
                code = (str(exc).split(":", 1)[0] if isinstance(exc, Error) else
                        "STORAGE_IO_ERROR" if isinstance(exc, OSError) else "STATE_INVALID")
                if args.operation != "run":
                    raise
                print(json.dumps({"health": "DEGRADED", "error_code": code}), flush=True)
            if args.operation != "run":
                return 0
            time.sleep(30)  # config/pause and retry are observed independently of cadence
    except (Error, OSError, ValueError, KeyError, TypeError) as exc:
        code = (str(exc).split(":", 1)[0] if isinstance(exc, Error) else
                "STORAGE_IO_ERROR" if isinstance(exc, OSError) else "STATE_INVALID")
        print(json.dumps({"ok": False, "error_code": code}), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
