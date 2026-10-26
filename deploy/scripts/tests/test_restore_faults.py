"""Executable failure/maintenance tests; real PostgreSQL coverage is separate."""
import contextlib
import multiprocessing
import os
from pathlib import Path
import signal
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from restore_postgres import BackupError, Restore, RestoreError, _archive_info


class Backend:
    def __init__(self, fault=None):
        self.fault = fault
        self.calls = []
        self.target = {"system_id": "fixture", "database": "isolated", "project": "test"}
        self.current = b"PGDMPoriginal"
        self.restored = None

    def call(self, name):
        self.calls.append(name)
        if self.fault == name:
            raise RestoreError("injected " + name)

    def preflight(self):
        self.call("preflight")
        return self.target

    def validate(self, path):
        self.call("validate")
        size, digest = _archive_info(path)
        if not path.read_bytes().startswith(b"PGDMP") or path.read_bytes().endswith(b"corrupt"):
            raise RestoreError("invalid archive")
        return {"size_bytes": size, "sha256": digest}

    def stop(self):
        self.call("stop")

    def assert_stopped(self):
        self.call("assert_stopped")

    def backup(self, path):
        self.call("backup")
        path.write_bytes(self.current)
        path.chmod(0o600)

    def recreate(self):
        self.call("recreate")
        self.current = b"PGDMPempty"

    def restore(self, path):
        self.call("restore")
        self.restored = path.read_bytes()
        self.current = self.restored



class Coordinator:
    def __init__(self, fail=False):
        self.reason = None
        self.fail = fail
        self.calls = []

    def pause(self, reason):
        self.calls.append("pause")
        if self.fail:
            raise RestoreError("injected pause failure")
        if self.reason is not None:
            raise RestoreError("already paused")
        self.reason = reason

    def assert_paused(self, reason):
        self.calls.append("assert_paused")
        if self.reason != reason:
            raise RestoreError("foreign pause reason")


class RestoreFaultTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.source = self.base / "legacy.dump"
        self.source.write_bytes(b"PGDMPincoming")
        os.environ["FLOWSTOCK_BACKUP_OUTPUT_DIR"] = str(self.base / "backups")
        self.backend = Backend()
        self.restore = Restore(self.backend, self.base / "runtime")

    def execute(self, **kwargs):
        with self.restore.lock(), contextlib.redirect_stdout(sys.stdout):
            self.restore.execute(self.source, **kwargs)

    def test_shared_pause_failure_blocks_stop_without_local_barrier(self):
        backend = Backend()
        coord = Coordinator(fail=True)
        restore = Restore(backend, self.base / "pause-fail", coordinator=coord)
        with restore.lock(), self.assertRaises(RestoreError):
            restore.execute(self.source)
        self.assertNotIn("stop", backend.calls)
        self.assertNotIn("recreate", backend.calls)
        self.assertFalse(restore.active.exists())

    def test_shared_pause_survives_failure_and_recovery(self):
        backend = Backend("restore")
        coord = Coordinator()
        restore = Restore(backend, self.base / "pause-recovery", coordinator=coord)
        with restore.lock(), self.assertRaises(RestoreError):
            restore.execute(self.source)
        first = restore.state["operation_id"]
        self.assertEqual(coord.reason, "restore:" + first)
        backend.fault = None
        with restore.lock():
            restore.execute(None, recover=first)
        self.assertEqual(restore.state["backup_pause_reason"], "restore:" + first)
        self.assertEqual(coord.calls.count("pause"), 1)
        with restore.lock():
            restore.complete(restore.state["operation_id"])
        # Explicit operator resume is separate from restore completion.
        self.assertEqual(coord.reason, "restore:" + first)

    def test_foreign_pause_blocks_recovery_before_sql(self):
        backend = Backend("restore")
        coord = Coordinator()
        restore = Restore(backend, self.base / "foreign-pause", coordinator=coord)
        with restore.lock(), self.assertRaises(RestoreError):
            restore.execute(self.source)
        original_active = restore.active.read_bytes()
        first = restore.state["operation_id"]
        coord.reason = "migration-maintenance"
        backend.fault = None
        count = backend.calls.count("recreate")
        with restore.lock(), self.assertRaises(RestoreError):
            restore.execute(None, recover=first)
        self.assertEqual(backend.calls.count("recreate"), count)
        self.assertEqual(restore.active.read_bytes(), original_active)

    def test_missing_empty_invalid_and_corrupt_input_never_stops(self):
        for payload in (None, b"", b"invalid header", b"PGDMPcorrupt"):
            with self.subTest(payload=payload):
                self.source.unlink(missing_ok=True)
                if payload is not None:
                    self.source.write_bytes(payload)
                with self.assertRaises((RestoreError, BackupError, OSError)):
                    self.execute()
                self.assertNotIn("stop", self.backend.calls)
                self.assertFalse(self.restore.active.exists())

    def test_output_same_path_hardlink_symlink_and_existing_copy_preserved(self):
        hardlink = self.base / "hardlink.dump"
        os.link(self.source, hardlink)
        symlink = self.base / "symlink.dump"
        symlink.symlink_to(self.source)
        existing = self.base / "existing.dump"
        existing.write_bytes(b"keep")
        for output in (self.source, hardlink, symlink, existing):
            with self.subTest(output=output):
                before = output.read_bytes()
                with self.assertRaises(RestoreError):
                    self.execute(safety_override=output)
                self.assertEqual(output.read_bytes(), before)
                self.assertNotIn("stop", self.backend.calls)

    def test_symlink_input_refused(self):
        alias = self.base / "input-link.dump"
        alias.symlink_to(self.source)
        self.source = alias
        with self.assertRaises(OSError):
            self.execute()
        self.assertNotIn("stop", self.backend.calls)

    def test_preflight_failure_has_no_stop_or_barrier(self):
        self.backend.fault = "preflight"
        with self.assertRaises(RestoreError):
            self.execute()
        self.assertNotIn("stop", self.backend.calls)
        self.assertFalse(self.restore.active.exists())

    def test_stop_failure_and_unconfirmed_stop_block_backup_and_drop(self):
        for fault in ("stop", "assert_stopped"):
            with self.subTest(fault=fault):
                restore = Restore(Backend(fault), self.base / fault)
                with restore.lock(), self.assertRaises(RestoreError):
                    restore.execute(self.source)
                self.assertNotIn("recreate", restore.backend.calls)
                self.assertTrue(restore.active.exists())

    def test_failed_empty_invalid_safety_prevents_destructive_calls(self):
        for payload in (b"", b"invalid", b"PGDMPcorrupt", None):
            with self.subTest(payload=payload):
                backend = Backend("backup" if payload is None else None)
                backend.current = payload
                restore = Restore(backend, self.base / ("case" + str(len(list(self.base.iterdir())))))
                with restore.lock(), self.assertRaises((RestoreError, BackupError, OSError)):
                    restore.execute(self.source)
                self.assertNotIn("recreate", backend.calls)
                self.assertTrue(restore.active.exists())
                self.assertFalse(restore.state["safety_validated"])
                self.assertEqual(self.source.read_bytes(), b"PGDMPincoming")

    def test_input_replacement_after_stop_does_not_change_consumed_archive(self):
        original_stop = self.backend.stop
        def stop():
            original_stop()
            self.source.write_bytes(b"corrupt replaced input")
        self.backend.stop = stop
        self.execute()
        self.assertEqual(self.backend.restored, b"PGDMPincoming")

    def test_sql_failure_keeps_safety_barrier_and_forbids_ordinary_retry(self):
        self.backend.fault = "restore"
        with self.assertRaises(RestoreError):
            self.execute()
        state = self.restore.state
        self.assertTrue(state["safety_validated"])
        self.assertEqual(Path(state["safety_path"]).read_bytes(), b"PGDMPoriginal")
        calls = list(self.backend.calls)
        with self.assertRaises(RestoreError):
            self.execute()
        self.assertEqual(self.backend.calls, calls)
        with self.restore.lock(), self.assertRaises(RestoreError):
            self.restore.complete(state["operation_id"])
        self.assertTrue(self.restore.active.exists())

    def test_recovery_failure_then_retry_preserves_original_good_state(self):
        self.backend.fault = "restore"
        with self.assertRaises(RestoreError):
            self.execute()
        original_safety = Path(self.restore.state["safety_path"])
        for _ in range(2):
            previous = self.restore.state["operation_id"]
            with self.assertRaises(RestoreError):
                self.execute(recover=previous)
            self.assertEqual(self.restore.state["recovery_source"]["path"], str(original_safety))
        self.backend.fault = None
        self.execute(recover=self.restore.state["operation_id"])
        self.assertEqual(self.backend.restored, b"PGDMPoriginal")
        self.assertEqual(original_safety.read_bytes(), b"PGDMPoriginal")
        with self.restore.lock():
            self.restore.complete(self.restore.state["operation_id"])
        self.assertFalse(self.restore.active.exists())

    def test_changed_target_or_tampered_safety_blocks_recovery(self):
        self.backend.fault = "restore"
        with self.assertRaises(RestoreError):
            self.execute()
        previous = self.restore.state.copy()
        self.backend.fault = None
        self.backend.target = self.backend.target | {"system_id": "other-cluster"}
        with self.assertRaises(RestoreError):
            self.execute(recover=previous["operation_id"])
        self.backend.target = previous["target"]
        Path(previous["safety_path"]).write_bytes(b"PGDMPtampered")
        with self.assertRaises(RestoreError):
            self.execute(recover=previous["operation_id"])

    def test_failed_recovery_before_drop_cannot_clear_prior_destructive_failure(self):
        self.backend.fault = "restore"
        with self.assertRaises(RestoreError):
            self.execute()
        previous = self.restore.state["operation_id"]
        self.backend.fault = "backup"
        with self.assertRaises(RestoreError):
            self.execute(recover=previous)
        self.assertFalse(self.restore.state["destructive_started"])
        with self.restore.lock(), self.assertRaises(RestoreError):
            self.restore.complete(self.restore.state["operation_id"])
        self.assertTrue(self.restore.active.exists())

    def test_sigkill_releases_lock_but_durable_barrier_prevents_retry(self):
        ctx = multiprocessing.get_context("fork")
        ready = ctx.Event()
        def child():
            def kill_at_recreate():
                ready.set()
                signal.pause()
            self.backend.recreate = kill_at_recreate
            self.execute()
        process = ctx.Process(target=child)
        process.start()
        self.addCleanup(lambda: process.is_alive() and process.kill())
        self.assertTrue(ready.wait(10))
        with self.assertRaises(RestoreError), self.restore.lock():
            pass
        process.kill()
        process.join(10)
        self.assertEqual(process.exitcode, -signal.SIGKILL)
        with self.assertRaises(RestoreError):
            self.execute()
        active = __import__("json").loads(self.restore.active.read_text())
        previous = self.restore.previous(active["operation_id"])
        self.assertTrue(previous["destructive_started"])
        self.assertTrue(previous["safety_validated"])
        self.assertTrue(Path(previous["safety_path"]).exists())


if __name__ == "__main__":
    unittest.main()
