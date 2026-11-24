#!/usr/bin/env python3
"""Scheduler behavior with a fake UTC clock and isolated fault-injected tools."""
import contextlib
import io
import json
import os
from pathlib import Path
import signal
import sys
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import backup_scheduler as scheduler
import test_managed_backup as fixtures

core = scheduler.core


class SchedulerTests(unittest.TestCase):
    def setUp(self):
        fixtures.ManagedBackupFaultTests.setUp(self)
        patch = mock.patch.dict(os.environ, self.env)
        patch.start()
        self.addCleanup(patch.stop)
        self.now = [1795000000.0]
        patch = mock.patch.object(core, "utc_now", lambda: scheduler.stamp(self.now[0]))
        patch.start()
        self.addCleanup(patch.stop)
        self.store = core.Store(str(self.root))
        self.engine = self.restart()
        self.engine.bootstrap()

    def restart(self):
        return scheduler.Scheduler(self.store, clock=lambda: self.now[0], expected_bytes=1024)

    def config(self, *, enabled=True, hours=24, count=30, days=30):
        value = scheduler.default_config()
        value["scheduler"].update(enabled=enabled, interval_hours=hours)
        value["local"].update(keep_count=count, keep_days=days)
        return value

    def configure(self, **kwargs):
        value = self.config(**kwargs)
        applied = self.engine.bootstrap()
        revision = self.engine.requested(applied)["revision"]
        preview = self.engine.preview(value, revision)
        return self.engine.configure(value, revision, preview["confirmation_token"])

    def tick(self):
        with self.store.lock(2), contextlib.redirect_stdout(io.StringIO()):
            return self.engine.tick()

    def seed(self, *, origin="scheduled", age=0, pins=None, remote=None):
        before = self.now[0]
        self.now[0] -= age
        try:
            item = self.store.create(origin, 1024)
        finally:
            self.now[0] = before
        if pins is not None:
            item["pins"] = pins
        if remote is not None:
            item["remote_state"] = remote
        scheduler.replace(self.store.catalog / (item["backup_id"] + ".json"), item)
        return item

    def state(self):
        return core._read(self.engine.folder / "state.json")

    def archive(self, item):
        return self.store.archives / (item["backup_id"] + ".dump")

    def test_new_installation_is_disabled_until_explicit_cutover(self):
        value = self.tick()
        self.assertEqual("DISABLED", value["health"])
        self.assertEqual([], list(self.store.archives.iterdir()))
        self.assertFalse(value["effective_config"]["scheduler"]["enabled"])

    def test_restart_within_interval_skips_and_overdue_catches_up_once(self):
        first = self.seed(age=23 * 3600)
        self.configure()
        self.tick()
        self.engine = self.restart()
        self.tick()
        self.assertEqual(1, len(self.engine.records()))
        self.now[0] += 10 * 86400
        self.tick()
        self.engine = self.restart()
        self.tick()
        self.assertEqual(2, len(self.engine.records()))
        self.assertNotEqual(first["backup_id"], self.state()["last_scheduled_backup_id"])

    def test_manual_and_safety_success_do_not_postpone_scheduled_deadline(self):
        item = self.seed(age=25 * 3600)
        self.seed(origin="manual")
        self.seed(origin="pre_deploy")
        self.configure()
        self.tick()
        self.assertEqual(4, len(self.engine.records()))
        self.assertNotEqual(item["backup_id"], self.state()["last_scheduled_backup_id"])

    def test_legacy_import_never_becomes_scheduled_success(self):
        item = self.seed(origin="manual")
        item["origin"] = "legacy_import"
        scheduler.replace(self.store.catalog / (item["backup_id"] + ".json"), item)
        self.configure()
        value = self.tick()
        self.assertIsNone(value["schedule"]["last_scheduled_success_at_utc"])
        self.assertEqual("OPERATOR_ACTION_REQUIRED", value["error_code"])
        self.assertEqual(1, len(self.engine.records()))
        self.now[0] += 300
        self.tick()
        self.assertIsNotNone(self.state()["last_scheduled_success_at_utc"])

    def test_failure_backoff_is_durable_and_never_advances_success(self):
        first = self.seed(age=25 * 3600)
        self.configure()
        with mock.patch.dict(os.environ, {"DUMP_FAIL": "1"}):
            for delay in (300, 900, 3600, 3600):
                result = self.tick()
                state = self.state()
                self.assertEqual(first["completed_at_utc"], state["last_scheduled_success_at_utc"])
                self.assertEqual(self.now[0] + delay, scheduler.seconds(state["next_retry_at_utc"]))
                self.assertEqual("DUMP_FAILED", result["error_code"])
                pending = len(list(self.store.staging.iterdir()))
                self.engine = self.restart()
                self.tick()
                self.assertEqual(pending, len(list(self.store.staging.iterdir())))
                self.assertFalse(list(self.store.jobs.glob("*.json")))
                self.now[0] += delay
        self.tick()
        self.assertEqual(0, self.state()["failures"])
        self.assertEqual(2, len(self.engine.records()))

    def test_clock_rollback_rebases_retry_and_forward_jump_never_replays_intervals(self):
        self.seed(age=25 * 3600)
        self.configure()
        with mock.patch.dict(os.environ, {"DUMP_FAIL": "1"}):
            self.tick()
            self.now[0] -= 10000
            self.tick()
            self.assertEqual(self.now[0] + 300, scheduler.seconds(self.state()["next_retry_at_utc"]))
        self.now[0] += 100 * 86400
        self.tick()
        self.tick()
        self.assertEqual(2, len(self.engine.records()))

    def test_long_dump_anchors_deadline_to_completion(self):
        self.seed(age=25 * 3600)
        self.configure()
        original = self.store.create
        def slow(*args, **kwargs):
            self.now[0] += 3 * 3600
            return original(*args, **kwargs)
        with mock.patch.object(self.store, "create", slow):
            self.tick()
        self.assertEqual(self.now[0] + 86400, scheduler.seconds(self.state()["next_run_at_utc"]))
        self.tick()
        self.assertEqual(2, len(self.engine.records()))

    def test_pause_and_disabled_skip_then_reenable_one_overdue_attempt(self):
        self.seed(age=25 * 3600)
        self.configure()
        self.store.pause("restore:fixture")
        self.assertEqual("PAUSED", self.tick()["health"])
        self.assertEqual(1, len(self.engine.records()))
        self.store.resume()
        self.configure(enabled=False)
        self.assertEqual("DISABLED", self.tick()["health"])
        self.configure()
        self.tick()
        self.assertEqual(2, len(self.engine.records()))

    def test_missing_and_corrupt_state_rebuild_only_from_validated_catalog(self):
        item = self.seed(age=3600)
        self.configure()
        self.tick()
        path = self.engine.folder / "state.json"
        for payload in (None, "{broken", "{}", '{"schema_version":1,"failures":0,"job_state":"IDLE"}'):
            if payload is None:
                path.unlink()
            else:
                path.write_text(payload)
            self.engine = self.restart()
            self.tick()
            self.assertEqual(item["backup_id"], self.state()["last_scheduled_backup_id"])
            self.assertEqual(1, len(self.engine.records()))
        self.assertEqual(3, len(list(self.engine.folder.glob("*.invalid"))))

    def test_state_future_schema_and_corrupt_archive_fail_closed(self):
        item = self.seed()
        self.configure()
        self.tick()
        original = self.state()
        scheduler.replace(self.engine.folder / "state.json", original | {"schema_version": 2})
        self.assertEqual("UNSUPPORTED_SCHEMA", self.tick()["error_code"])
        scheduler.replace(self.engine.folder / "state.json", original)
        self.archive(item).write_bytes(b"VALID:CHANGED:END")
        self.assertEqual("DEGRADED", self.tick()["health"])
        self.assertEqual(1, len(list(self.store.archives.iterdir())))

    def test_mtime_cannot_produce_a_fake_success(self):
        (self.root / "FlowStock_20260101_000000.dump").write_bytes(b"unknown")
        self.configure()
        result = self.tick()
        self.assertIsNone(result["schedule"]["last_scheduled_success_at_utc"])
        self.assertEqual([], self.engine.records())

    def test_configuration_is_validated_atomically_and_acknowledged_by_worker(self):
        self.seed()
        request = self.configure()
        self.assertEqual("PENDING", request["apply_state"])
        self.assertEqual(0, request["applied_revision"])
        result = self.tick()
        self.assertEqual(1, result["applied_revision"])
        self.assertEqual("APPLIED", result["apply_state"])
        with self.assertRaisesRegex(core.BackupError, "CONFIG_CONFLICT"):
            self.engine.configure(self.config(hours=6), 0)
        for mutate in (
                lambda c: c["scheduler"].update(interval_hours=5),
                lambda c: c["scheduler"].update(interval_hours=169),
                lambda c: c["scheduler"].update(enabled=1),
                lambda c: c["local"].update(keep_count=6),
                lambda c: c["local"].update(keep_days=366),
                lambda c: c["local"].update(storage_root_alias="../../etc"),
                lambda c: c.update(remote={"enabled": True})):
            value = self.config()
            mutate(value)
            with self.assertRaises(core.BackupError):
                self.engine.configure(value, 1)
        self.assertEqual(1, self.engine.requested(self.engine.bootstrap())["revision"])

    def test_missing_applied_config_never_silently_bootstraps_over_old_revisions(self):
        (self.engine.config / "applied.json").unlink()
        with self.assertRaisesRegex(core.BackupError, "CONFIG_MISSING"):
            self.engine.bootstrap()

    def test_retention_reduction_requires_fresh_bound_preview(self):
        self.seed()
        config = self.config(count=7)
        with self.assertRaisesRegex(core.BackupError, "RETENTION_CONFIRMATION_REQUIRED"):
            self.engine.configure(config, 0)
        preview = self.engine.preview(config, 0)
        self.seed(origin="manual")
        with self.assertRaisesRegex(core.BackupError, "RETENTION_CONFIRMATION_REQUIRED"):
            self.engine.configure(config, 0, preview["confirmation_token"])
        token = self.engine.preview(config, 0)["confirmation_token"]
        self.engine.configure(config, 0, token)

    def test_count_retention_only_prunes_managed_verified_unprotected_copies(self):
        old = [self.seed(age=(i + 1) * 3600, origin="manual") for i in range(9)]
        latest = self.seed()
        legacy = self.root / "FlowStock_20260101_000000.dump"
        legacy.write_bytes(b"legacy-preserved")
        unknown = self.store.archives / "foreign.dump"
        unknown.write_bytes(b"foreign-preserved")
        self.configure(count=7)
        result = self.tick()
        self.assertEqual(3, len(result["retention"]["pruned"]))
        self.assertEqual(7, len(self.engine.present(self.engine.records())))
        self.assertTrue(self.archive(latest).exists())
        self.assertEqual(b"legacy-preserved", legacy.read_bytes())
        self.assertEqual(b"foreign-preserved", unknown.read_bytes())
        for item in old[-3:]:
            self.assertFalse(self.archive(item).exists())
            saved = core._read(self.store.catalog / (item["backup_id"] + ".json"))
            self.assertEqual("PRUNED", saved["local_presence"])
        self.engine = self.restart()
        self.assertEqual("HEALTHY", self.tick()["health"])

    def test_age_is_or_count_and_boundary_is_strict(self):
        old = self.seed(origin="manual", age=7 * 86400 + 1)
        boundary = self.seed(origin="manual", age=7 * 86400)
        latest = self.seed()
        self.configure(days=7)
        self.tick()
        self.assertFalse(self.archive(old).exists())
        self.assertTrue(self.archive(boundary).exists())
        self.assertTrue(self.archive(latest).exists())

    def test_pins_safety_remote_backlog_and_last_scheduled_are_protected(self):
        held = [self.seed(origin="manual", age=40 * 86400, pins=["recovery"]),
                self.seed(origin="pre_deploy", age=40 * 86400),
                self.seed(origin="pre_restore", age=40 * 86400),
                self.seed(origin="manual", age=40 * 86400, remote="RETRY_WAIT"),
                self.seed(origin="scheduled", age=20 * 86400),
                self.seed(origin="manual")]
        self.configure(days=7)
        # Avoid an overdue dump so that last scheduled protection is exercised.
        self.configure(enabled=False, days=7)
        self.tick()
        result = self.engine.prune(self.engine.records(), self.config(days=7))
        self.assertFalse(result["pruned"])
        self.assertTrue(result["protected_over_limit"])
        for item in held:
            self.assertTrue(self.archive(item).exists())

    def test_low_disk_never_deletes_backups_or_advances_success(self):
        item = self.seed(age=40 * 86400)
        self.configure(days=7)
        usage = type(shutil_usage())(10000, 9500, 500)
        with mock.patch.object(core.shutil, "disk_usage", return_value=usage):
            result = self.tick()
        self.assertEqual("STORAGE_LOW", result["error_code"])
        self.assertEqual(item["completed_at_utc"], self.state()["last_scheduled_success_at_utc"])
        self.assertTrue(self.archive(item).exists())

    def test_symlink_and_foreign_version_block_cleanup_before_first_delete(self):
        old = self.seed(origin="manual", age=40 * 86400)
        self.seed()
        self.configure(days=7)
        record = self.store.catalog / (old["backup_id"] + ".json")
        original = core._read(record)
        scheduler.replace(record, original | {"schema_version": 2})
        self.assertEqual("UNSUPPORTED_SCHEMA", self.tick()["error_code"])
        self.assertTrue(self.archive(old).exists())
        scheduler.replace(record, original)
        archive = self.archive(old)
        saved = self.root / "saved.dump"
        archive.rename(saved)
        archive.symlink_to(saved)
        self.assertEqual("DEGRADED", self.tick()["health"])
        self.assertTrue(saved.exists())

    def test_status_is_read_only_and_exposes_pending_config(self):
        self.seed()
        self.tick()
        self.configure()
        before = {p: (p.stat().st_mtime_ns, p.read_bytes()) for p in self.store.managed.rglob("*") if p.is_file()}
        result = scheduler.status(str(self.root))
        after = {p: (p.stat().st_mtime_ns, p.read_bytes()) for p in self.store.managed.rglob("*") if p.is_file()}
        self.assertEqual(before, after)
        self.assertEqual("PENDING", result["apply_state"])
        self.assertFalse(result["effective_config"]["scheduler"]["enabled"])
        self.assertGreater(result["last_scheduled_archive"]["size_bytes"], 0)
        self.assertFalse(result["structural_validation_is_restore_proof"])

    def test_two_simultaneous_workers_create_only_one_scheduled_dump(self):
        self.seed(age=25 * 3600)
        self.configure()
        children = []
        for _ in range(2):
            pid = os.fork()
            if pid == 0:
                try:
                    self.tick()
                    os._exit(0)
                except BaseException:
                    os._exit(1)
            children.append(pid)
        for pid in children:
            self.assertEqual(0, os.waitpid(pid, 0)[1])
        self.assertEqual(2, len(self.engine.records()))

    def test_concurrent_manual_holds_same_lock_without_moving_planned_deadline(self):
        import time
        self.seed(age=25 * 3600)
        self.configure()
        ready = self.root / "manual-locked"
        pid = os.fork()
        if pid == 0:
            with self.store.lock(2):
                ready.touch()
                time.sleep(0.3)
                self.store.create("manual", 1024)
            os._exit(0)
        deadline = time.monotonic() + 3
        while not ready.exists() and time.monotonic() < deadline:
            time.sleep(0.01)
        self.assertTrue(ready.exists())
        self.tick()
        self.assertEqual(0, os.waitpid(pid, 0)[1])
        records = self.engine.records()
        self.assertEqual(3, len(records))
        self.assertEqual(2, sum(r["origin"] == "scheduled" for r in records))

    def test_uncertain_ready_publication_blocks_retry_and_retention(self):
        item = self.seed(age=25 * 3600)
        self.configure()
        pending = self.store._pending("scheduled")
        stage = self.store.staging / (pending["backup_id"] + ".part")
        stage.write_bytes(b"VALID:TRUNCATED")
        stage.chmod(0o600)
        self.store._ready(pending, stage, core._archive_info(stage))
        self.assertEqual("UNKNOWN_OUTCOME", self.tick()["error_code"])
        self.assertTrue(self.archive(item).exists())
        self.assertTrue(stage.exists())
        self.assertEqual(1, len(self.engine.records()))

    def test_corrupt_requested_config_keeps_previous_applied_and_blocks_operations(self):
        self.seed(age=25 * 3600)
        self.configure()
        path = self.engine.config / "requested.json"
        path.write_text("{broken")
        result = self.tick()
        self.assertEqual("REJECTED", result["apply_state"])
        self.assertEqual(0, self.engine.bootstrap()["revision"])
        self.assertEqual(1, len(self.engine.records()))

    def test_unexpected_missing_is_not_a_retention_tombstone(self):
        old = self.seed(origin="manual", age=40 * 86400)
        self.seed()
        self.configure(days=7)
        self.archive(old).unlink()
        self.assertEqual("DEGRADED", self.tick()["health"])
        self.assertFalse(list(self.engine.prunes.iterdir()))
        self.assertEqual("PRESENT", core._read(self.store.catalog / (old["backup_id"] + ".json"))["local_presence"])

    def test_stale_observation_does_not_claim_operational_health(self):
        self.seed()
        self.tick()
        path = self.engine.folder / "observation.json"
        value = core._read(path)
        scheduler.replace(path, value | {"observed_at_utc": "2020-01-01T00:00:00Z"})
        result = scheduler.status(str(self.root))
        self.assertEqual("WORKER_UNAVAILABLE", result["error_code"])

    def test_kill_after_catalog_before_success_snapshot_recovers_without_duplicate(self):
        self.seed(age=25 * 3600)
        self.configure()
        self.tick_with_kill(after_catalog=True)
        self.engine = self.restart()
        self.tick()
        self.assertEqual(2, len(self.engine.records()))
        self.assertEqual("IDLE", self.state()["job_state"])

    def test_kill_before_dump_persists_interrupted_retry(self):
        self.seed(age=25 * 3600)
        self.configure()
        self.tick_with_kill(after_catalog=False)
        self.engine = self.restart()
        self.tick()
        self.assertEqual(1, len(self.engine.records()))
        self.assertEqual("INTERRUPTED", self.state()["error_code"])
        self.assertEqual(self.now[0] + 300, scheduler.seconds(self.state()["next_retry_at_utc"]))

    def tick_with_kill(self, *, after_catalog):
        pid = os.fork()
        if pid == 0:
            original = self.engine.save_state
            def save(state):
                if after_catalog and state["last_scheduled_success_at_utc"] == scheduler.stamp(self.now[0]):
                    os.kill(os.getpid(), signal.SIGKILL)
                original(state)
                if not after_catalog and state["job_state"] == "RUNNING":
                    os.kill(os.getpid(), signal.SIGKILL)
            self.engine.save_state = save
            self.tick()
            os._exit(2)
        _, status = os.waitpid(pid, 0)
        self.assertTrue(os.WIFSIGNALED(status))
        self.assertEqual(signal.SIGKILL, os.WTERMSIG(status))

    def test_prune_crash_after_unlink_is_reconciled_to_durable_tombstone(self):
        old = self.seed(origin="manual", age=40 * 86400)
        self.seed()
        self.configure(days=7)
        pid = os.fork()
        if pid == 0:
            original = scheduler.replace
            def replace(path, value):
                if path.parent == self.store.catalog and value.get("local_presence") == "PRUNED":
                    os.kill(os.getpid(), signal.SIGKILL)
                return original(path, value)
            scheduler.replace = replace
            self.tick()
            os._exit(2)
        _, status = os.waitpid(pid, 0)
        self.assertTrue(os.WIFSIGNALED(status))
        self.engine = self.restart()
        self.assertEqual("HEALTHY", self.tick()["health"])
        self.assertFalse(self.archive(old).exists())
        record = core._read(self.store.catalog / (old["backup_id"] + ".json"))
        self.assertEqual("PRUNED", record["local_presence"])


def shutil_usage():
    import shutil
    return shutil.disk_usage("/")


if __name__ == "__main__":
    unittest.main()
