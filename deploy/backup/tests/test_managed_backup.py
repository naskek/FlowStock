#!/usr/bin/env python3
"""Fault/regression tests for the managed backup primitive. No database needed."""
import contextlib
import fcntl
import importlib.util
import json
import os
from pathlib import Path
import shutil
import signal
import stat
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock

TOOL = Path(__file__).resolve().parents[1] / "managed_backup.py"
spec = importlib.util.spec_from_file_location("flowstock_managed_backup", TOOL)
core = importlib.util.module_from_spec(spec)
spec.loader.exec_module(core)


class ManagedBackupFaultTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name) / "backups"
        self.root.mkdir()
        self.bin = Path(self.tmp.name) / "bin"
        self.bin.mkdir()
        dump = self.bin / "pg_dump"
        dump.write_text("""#!/usr/bin/env python3
import os,sys,time
args=sys.argv[1:]
if "--version" in args:
    print("pg_dump (PostgreSQL) 16.10")
    sys.exit(0)
if os.environ.get("DUMP_FAIL") == "1":
    sys.exit(1)
if os.environ.get("SLEEP_DUMP"):
    time.sleep(float(os.environ["SLEEP_DUMP"]))
if "-f" not in args:
    sys.exit(1)
dest=args[args.index("-f")+1]
with open(dest,"wb") as f:
    f.write(b"BAD" if os.environ.get("DUMP_BAD") else b"VALID:BEGIN:ROWS:END")
""")
        restore = self.bin / "pg_restore"
        restore.write_text("""#!/usr/bin/env python3
import os,sys
if "--version" in sys.argv:
    print("pg_restore (PostgreSQL) 16.10")
    sys.exit(0)
p=sys.argv[-1]
try:
    data=open(p,"rb").read()
except OSError:
    sys.exit(1)
if not data.startswith(b"VALID:"):
    sys.exit(1)
if "--list" not in sys.argv and (os.environ.get("PAYLOAD_FAIL") or not data.endswith(b":END")):
    sys.exit(1)
""")
        psql = self.bin / "psql"
        psql.write_text("#!/bin/sh\nprintf '1|fixture.sql\\n'\n")
        for p in [dump, restore, psql]:
            p.chmod(0o700)
        self.env = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ["PATH"])
        self.script = [sys.executable, str(TOOL), "--root", str(self.root)]

    def call(self, *args, extra=None):
        return subprocess.run([*self.script, *args], capture_output=True,
                              text=True, env=self.env | (extra or {}), timeout=20)

    def make(self, *args):
        result = self.call("create", "--origin", "scheduled", "--expected-bytes", "1024", *args)
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(result.stdout)

    def paths(self):
        return (list((self.root / "managed" / "archives").glob("*.dump")),
                list((self.root / "managed" / "catalog").glob("*.json")))

    def test_valid_archive_and_catalog_are_published_together(self):
        item = self.make()
        dumps, catalog = self.paths()
        self.assertEqual(len(dumps), 1)
        self.assertEqual(len(catalog), 1)
        self.assertEqual(item["local_state"], "LOCAL_VALID")
        self.assertEqual(item["origin"], "scheduled")
        self.assertEqual(item["schema_migrations_state"], "KNOWN")
        self.assertEqual(item["structural_validated"], True)
        self.assertIsNone(item["restore_test_at_utc"])
        self.assertEqual(stat.S_IMODE(dumps[0].stat().st_mode), 0o600)
        self.assertEqual(stat.S_IMODE(catalog[0].stat().st_mode), 0o600)
        self.assertEqual(item["sha256"], core.hashlib.sha256(dumps[0].read_bytes()).hexdigest())

    def test_json_publication_survives_sigkill_at_each_boundary(self):
        for phase in ("before_rename", "after_rename", "after_directory_fsync"):
            for kind in ("pause", "job", "ready", "catalog"):
                with self.subTest(phase=phase, kind=kind):
                    folder = self.root / (phase + "-" + kind)
                    folder.mkdir(mode=0o700)
                    target = folder / "record.json"
                    pid = os.fork()
                    if pid == 0:
                        original_rename = core._rename_new
                        original_fsync = core._fsync_dir
                        def rename(source, destination):
                            if phase == "before_rename":
                                os.kill(os.getpid(), signal.SIGKILL)
                            original_rename(source, destination)
                            if phase == "after_rename":
                                os.kill(os.getpid(), signal.SIGKILL)
                        def sync(directory):
                            original_fsync(directory)
                            if phase == "after_directory_fsync":
                                os.kill(os.getpid(), signal.SIGKILL)
                        core._rename_new = rename
                        core._fsync_dir = sync
                        core._write_new(target, {"kind": kind})
                        os._exit(0)
                    _, status = os.waitpid(pid, 0)
                    self.assertTrue(os.WIFSIGNALED(status))
                    self.assertEqual(os.WTERMSIG(status), signal.SIGKILL)
                    if phase == "before_rename":
                        self.assertFalse(target.exists())
                    else:
                        self.assertEqual(core._read(target), {"kind": kind})
                        self.assertEqual(target.stat().st_nlink, 1)

    def test_json_publication_never_replaces_existing_path(self):
        target = self.root / "record.json"
        core._write_new(target, {"saved": True})
        with self.assertRaises(FileExistsError):
            core._write_new(target, {"saved": False})
        self.assertEqual(core._read(target), {"saved": True})
        alias = self.root / "alias.json"
        alias.symlink_to(target)
        with self.assertRaises(FileExistsError):
            core._write_new(alias, {"saved": False})
        self.assertTrue(alias.is_symlink())
        self.assertEqual(core._read(target), {"saved": True})

    def test_repeated_requests_have_unique_ids_and_never_overwrite(self):
        a, b = self.make(), self.make()
        self.assertNotEqual(a["backup_id"], b["backup_id"])
        dumps, _ = self.paths()
        self.assertEqual(len(dumps), 2)

    def test_dump_failure_does_not_publish(self):
        r = self.call("create", "--origin", "manual", "--expected-bytes", "1024",
                      extra={"DUMP_FAIL": "1"})
        self.assertNotEqual(r.returncode, 0)
        self.assertEqual(self.paths(), ([], []))
        self.assertEqual(len(list((self.root / "managed" / "jobs").glob("*.json"))), 1)

    def test_invalid_toc_and_corrupt_payload_fail_closed(self):
        for env in [{"DUMP_BAD": "1"}, {"PAYLOAD_FAIL": "1"}]:
            with self.subTest(env=env):
                r = self.call("create", "--origin", "manual", "--expected-bytes", "1024", extra=env)
                self.assertNotEqual(r.returncode, 0)
                self.assertEqual(self.paths(), ([], []))

    def test_recovery_after_crash_before_publication(self):
        s = core.Store(str(self.root))
        with mock.patch.dict(os.environ, {"PATH": self.env["PATH"]}):
            with mock.patch.object(s, "_publish", side_effect=RuntimeError("simulated kill")):
                with self.assertRaises(RuntimeError):
                    s.create("manual", 1024)
        self.assertEqual(self.paths(), ([], []))
        with mock.patch.dict(os.environ, {"PATH": self.env["PATH"]}):
            with s.lock(1):
                out = s.recover()
        self.assertEqual(len(out["recovered"]), 1)
        self.assertEqual(len(self.paths()[0]), 1)

    def test_recovery_after_crash_between_archive_and_manifest(self):
        s = core.Store(str(self.root))
        original = s._publish
        def interrupted(item, stage):
            target = s.archives / (item["backup_id"] + ".dump")
            os.link(stage, target)
            # Mimic kill immediately after archive link; two hardlinks remain.
            raise RuntimeError("crash")
        with mock.patch.dict(os.environ, {"PATH": self.env["PATH"]}):
            with mock.patch.object(s, "_publish", side_effect=interrupted):
                with self.assertRaises(RuntimeError):
                    s.create("manual", 1024)
        with mock.patch.dict(os.environ, {"PATH": self.env["PATH"]}):
            with s.lock(1):
                self.assertEqual(len(s.recover()["recovered"]), 1)
        self.assertEqual(len(self.paths()[1]), 1)
        self.assertEqual(len(list(s.staging.glob("*.part"))), 0)

    def test_recovery_rejects_modified_ready_stage_before_final_publish(self):
        s = core.Store(str(self.root))
        with mock.patch.dict(os.environ, {"PATH": self.env["PATH"]}):
            with mock.patch.object(s, "_publish", side_effect=RuntimeError("simulated kill")):
                with self.assertRaises(RuntimeError):
                    s.create("manual", 1024)
        stage = next(s.staging.glob("*.part"))
        stage.write_bytes(b"VALID:MODIFIED:ROWS:END")
        with mock.patch.dict(os.environ, {"PATH": self.env["PATH"]}):
            with s.lock(1):
                state = s.recover()
        self.assertTrue(state["operator_review_required"])
        self.assertEqual(self.paths(), ([], []))

    def test_recovery_never_calls_corrupt_staging_success(self):
        r = self.call("create", "--origin", "manual", "--expected-bytes", "1024",
                      extra={"DUMP_BAD": "1"})
        self.assertNotEqual(r.returncode, 0)
        rec = self.call("recover")
        self.assertEqual(rec.returncode, 0, rec.stderr)
        self.assertTrue(json.loads(rec.stdout)["operator_review_required"])
        self.assertEqual(self.paths(), ([], []))

    def test_pause_is_durable_and_pre_restore_needs_explicit_allowance(self):
        self.assertEqual(self.call("pause", "--reason", "restore:fixture").returncode, 0)
        denied = self.call("create", "--origin", "manual", "--expected-bytes", "1024")
        self.assertIn("MAINTENANCE_PAUSED", denied.stderr)
        self.assertEqual(self.paths(), ([], []))
        allow = self.call("create", "--origin", "pre_restore",
                          "--during-maintenance", "--expected-bytes", "1024")
        self.assertEqual(allow.returncode, 0, allow.stderr)
        unauthorized = self.call("create", "--origin", "pre_deploy",
                                  "--during-maintenance", "--expected-bytes", "1024")
        self.assertIn("MAINTENANCE_PAUSED", unauthorized.stderr)
        self.assertEqual(self.call("resume", "--operator-confirmed").returncode, 0)
        self.make()

    def test_predeploy_pause_only_allows_explicit_fresh_safety_origin(self):
        self.assertEqual(self.call("pause", "--reason", "pre_deploy:" + "a" * 40).returncode, 0)
        denied = self.call("create", "--origin", "scheduled", "--expected-bytes", "1024")
        self.assertIn("MAINTENANCE_PAUSED", denied.stderr)
        denied_restore = self.call("create", "--origin", "pre_restore",
                                   "--during-maintenance", "--expected-bytes", "1024")
        self.assertIn("MAINTENANCE_PAUSED", denied_restore.stderr)
        allowed = self.call("create", "--origin", "pre_deploy",
                            "--during-maintenance", "--expected-bytes", "1024")
        self.assertEqual(allowed.returncode, 0, allowed.stderr)
        self.assertEqual(json.loads(allowed.stdout)["origin"], "pre_deploy")

    def test_lock_timeout_and_release(self):
        store = core.Store(str(self.root))
        fd = os.open(store.maintenance / "lock", os.O_RDWR | os.O_CREAT, 0o600)
        fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
        try:
            denied = self.call("--lock-timeout", "0.2", "create", "--origin",
                               "manual", "--expected-bytes", "1024")
            self.assertIn("BACKUP_BUSY", denied.stderr)
        finally:
            os.close(fd)
        self.make()

    def test_kernel_lock_releases_after_sigkill(self):
        store = core.Store(str(self.root))
        lock = store.maintenance / "lock"
        fd = os.open(lock, os.O_RDWR | os.O_CREAT, 0o600)
        os.close(fd)
        child = subprocess.Popen(
            [sys.executable, "-c",
             "import fcntl,sys,time; "
             "f=open(sys.argv[1], 'r+'); "
             "fcntl.flock(f,fcntl.LOCK_EX); "
             "print('locked',flush=True); time.sleep(30)",
             str(lock)],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            self.assertEqual(child.stdout.readline().strip(), "locked")
            denied = self.call("--lock-timeout", "0.2", "status")
            self.assertIn("BACKUP_BUSY", denied.stderr)
        finally:
            child.kill()
            child.wait(timeout=5)
            child.stdout.close()
            child.stderr.close()
        result = self.call("status")
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_symlink_traversal_and_legacy_import_without_source_deletion(self):
        original = self.root / "FlowStock_20261019_135003.dump"
        original.write_bytes(b"VALID:BEGIN:ROWS:END")
        imported = self.call("import-legacy", "--name", original.name,
                             "--expected-bytes", "1024")
        self.assertEqual(imported.returncode, 0, imported.stderr)
        self.assertTrue(original.exists())
        record = json.loads(imported.stdout)
        self.assertEqual(record["origin"], "legacy_import")
        self.assertEqual(record["schema_migrations_state"], "UNKNOWN")
        self.assertIsNone(record["schema_migrations_fingerprint"])
        self.assertEqual(len(self.paths()[0]), 1)
        traversal = self.call("import-legacy", "--name", "../outside.dump",
                              "--expected-bytes", "1024")
        self.assertNotEqual(traversal.returncode, 0)
        alias = self.root / "FlowStock_20261020_135003.dump"
        alias.symlink_to(original)
        bad = self.call("import-legacy", "--name", alias.name, "--expected-bytes", "1024")
        self.assertNotEqual(bad.returncode, 0)

    def test_managed_symlink_is_rejected(self):
        (self.root / "managed").symlink_to(self.tmp.name, target_is_directory=True)
        r = self.call("status")
        self.assertNotEqual(r.returncode, 0)

    def test_disk_pressure_fails_without_deleting_existing_archive(self):
        created = self.make()
        store = core.Store(str(self.root))
        bad_usage = shutil._ntuple_diskusage(total=1000, used=990, free=10)
        with mock.patch.object(core.shutil, "disk_usage", return_value=bad_usage):
            with self.assertRaisesRegex(core.BackupError, "STORAGE_LOW"):
                store.create("scheduled", 1024)
        self.assertEqual(len(self.paths()[0]), 1)
        self.assertEqual(created["local_state"], "LOCAL_VALID")

    def test_corrupt_existing_catalog_blocks_new_backup_without_deletion(self):
        self.make()
        p = self.paths()[1][0]
        p.write_text("{invalid")
        r = self.call("create", "--origin", "manual", "--expected-bytes", "1024")
        self.assertNotEqual(r.returncode, 0)
        self.assertEqual(len(self.paths()[0]), 1)


if __name__ == "__main__":
    unittest.main(verbosity=2)
