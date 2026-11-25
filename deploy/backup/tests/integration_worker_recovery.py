#!/usr/bin/env python3
"""Legacy checkout/abort and managed recovery on an isolated PostgreSQL 16 stack."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import unittest
import uuid

REPO = Path(__file__).resolve().parents[3]


class WorkerRecoveryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix="flowstock-worker-recovery-")
        cls.base = Path(cls.temp.name)
        cls.script = cls.base / "pg_backup.sh"
        cls.script.write_text("#!/bin/sh\nexit 0\n", encoding="utf-8", newline="\n")
        cls.file = cls.base / "compose.yml"
        image = os.environ.get("FLOWSTOCK_TEST_BACKUP_IMAGE", "flowstock-pgbackup:local")
        cls.file.write_text('''services:
  postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: recovery_fixture
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD: isolated_ci_only
    tmpfs: [/var/lib/postgresql/data]
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres -d recovery_fixture"]
      interval: 1s
      timeout: 3s
      retries: 60
    restart: "no"
  legacy:
    image: postgres:16-alpine
    command: ["sh", "/usr/local/bin/pg_backup.sh"]
    volumes:
      - pgbackups:/backups
      - ''' + json.dumps(str(cls.script).replace("\\", "/") + ":/usr/local/bin/pg_backup.sh:ro") + '''
    restart: "no"
  pgbackup:
    image: ''' + image + '''
    command: ["sh", "/usr/local/bin/pg_backup.sh"]
    environment:
      PGHOST: postgres
      PGPORT: 5432
      PGDATABASE: recovery_fixture
      PGUSER: postgres
      PGPASSWORD: isolated_ci_only
      BACKUP_EXPECTED_BYTES: 1048576
    volumes:
      - pgbackups:/backups
    restart: "no"
volumes:
  pgbackups:
networks:
  default:
    internal: true
''', encoding="utf-8")
        cls.compose = ["docker", "compose", "-p", "flowstock-recovery-test-" + uuid.uuid4().hex[:10],
                       "-f", str(cls.file)]
        try:
            cls.run_command("up", "-d", "--wait", "postgres")
            cls.run_command("exec", "-T", "postgres", "psql", "-U", "postgres", "-d", "recovery_fixture",
                            "-v", "ON_ERROR_STOP=1", "-c", "CREATE TABLE invariant(id int PRIMARY KEY); INSERT INTO invariant VALUES(171);")
            cls.run_command("run", "--rm", "--no-deps", "-T", "--entrypoint", "sh", "pgbackup", "-ec",
                            "printf legacy-preserved >/backups/FlowStock_20261029_000000.dump")
        except BaseException:
            cls.run_command("down", "--volumes", "--remove-orphans", check=False)
            raise

    @classmethod
    def run_command(cls, *args, check=True):
        result = subprocess.run([*cls.compose, *args], text=True, capture_output=True, timeout=120)
        if check and result.returncode:
            raise AssertionError(result.stdout + result.stderr)
        return result

    @classmethod
    def manager(cls, *args, check=True, database=None):
        options = ["run", "--rm", "--no-deps", "-T"]
        if database:
            options += ["-e", "PGDATABASE=" + database]
        return cls.run_command(*options, "--entrypoint", "python3", "pgbackup",
                               "/usr/local/bin/managed_backup.py", "--root", "/backups",
                               "--lock-timeout", "10", *args, check=check)

    def tearDown(self):
        self.run_command("stop", "pgbackup")
        state = json.loads(self.manager("status").stdout)
        if state["paused"]:
            self.manager("resume", "--operator-confirmed")
        preserved = self.run_command("run", "--rm", "--no-deps", "-T", "--entrypoint", "cat", "pgbackup",
                                     "/backups/FlowStock_20261029_000000.dump")
        self.assertEqual("legacy-preserved", preserved.stdout)
        rows = self.run_command("exec", "-T", "postgres", "psql", "-U", "postgres", "-d", "recovery_fixture",
                                "-At", "-c", "SELECT id FROM invariant")
        self.assertEqual("171", rows.stdout.strip())

    @classmethod
    def tearDownClass(cls):
        cls.run_command("down", "--volumes", "--remove-orphans")
        cls.temp.cleanup()

    def test_legacy_container_after_checkout_and_abort_exits_incompatible(self):
        # Container is created BEFORE checkout. Its bind mount then observes the
        # new checkout, exactly as in the interrupted production deployment.
        self.run_command("create", "--no-build", "--pull", "never", "legacy")
        self.script.write_text((REPO / "deploy/backup/pg_backup.sh").read_text(encoding="utf-8"), encoding="utf-8", newline="\n")
        ident = self.run_command("ps", "-aq", "legacy").stdout.strip()
        subprocess.run(["docker", "start", "-a", ident], capture_output=True, text=True, timeout=30)
        inspect = subprocess.run(["docker", "inspect", ident], capture_output=True, text=True, check=True)
        state = json.loads(inspect.stdout)[0]["State"]
        self.assertFalse(state["Running"])
        self.assertEqual(78, state["ExitCode"])
        logs = self.run_command("logs", "legacy")
        self.assertIn("INCOMPATIBLE_BACKUP_RUNTIME: python3 is missing", logs.stdout + logs.stderr)
        self.assertNotIn("managed worker:", logs.stdout + logs.stderr)

    def test_missing_manager_is_a_permanent_failure(self):
        result = self.run_command("run", "--rm", "--no-deps", "-T", "--entrypoint", "sh", "pgbackup", "-ec",
                                  "rm /usr/local/bin/managed_backup.py; exec sh /usr/local/bin/pg_backup.sh", check=False)
        self.assertEqual(78, result.returncode)
        self.assertIn("managed_backup.py is missing", result.stderr)

    def test_failed_validation_preserves_maintenance(self):
        self.manager("pause", "--reason", "pre_deploy:fixture")
        result = self.manager("create", "--origin", "pre_deploy", "--during-maintenance",
                              "--expected-bytes", "1048576", database="does_not_exist", check=False)
        self.assertNotEqual(0, result.returncode)
        state = json.loads(self.manager("status").stdout)
        self.assertTrue(state["paused"])
        self.assertEqual("pre_deploy:fixture", state["pause_reason"])

    def test_managed_recovery_validates_before_resume_and_schedules_after(self):
        self.scheduler_python("/backups", '''
core.utc_now = lambda: backup_scheduler.stamp(time.time() - 25 * 3600)
store.create("scheduled", 1048576)
config = engine.bootstrap()["config"]
config["scheduler"]["enabled"] = True
engine.configure(config, engine.requested(engine.bootstrap())["revision"])
''')
        self.manager("pause", "--reason", "pre_deploy:fixture")
        self.run_command("up", "-d", "--no-deps", "--no-build", "--pull", "never", "pgbackup")
        self.run_command("exec", "-T", "pgbackup", "sh", "/usr/local/bin/pg_backup.sh", "--check-runtime")
        # Paused worker remains paused and never creates an unguarded dump.
        before = json.loads(self.manager("status").stdout)["archives"]
        time.sleep(3)
        self.assertEqual(before, json.loads(self.manager("status").stdout)["archives"])
        result = self.run_command("exec", "-T", "pgbackup", "python3", "/usr/local/bin/managed_backup.py",
                                  "--root", "/backups", "create", "--origin", "pre_deploy",
                                  "--during-maintenance", "--expected-bytes", "1048576")
        record = json.loads(result.stdout)
        self.assertEqual("LOCAL_VALID", record["local_state"])
        self.assertTrue(record["structural_validated"])
        self.assertEqual(16, record["pg_major"])
        self.assertGreater(record["size_bytes"], 0)
        self.assertEqual(before + 1, json.loads(self.manager("status").stdout)["archives"])
        self.manager("resume", "--operator-confirmed")
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            if json.loads(self.manager("status").stdout)["archives"] > before + 1:
                break
            time.sleep(1)
        else:
            self.fail("resumed worker did not create a new scheduled backup")
        logs = self.run_command("logs", "pgbackup")
        self.assertIn('"origin": "scheduled"', logs.stdout)

    @classmethod
    def scheduler_python(cls, root, body, *, now=None):
        script = '''import json,pathlib,time,sys
sys.path.insert(0, "/usr/local/bin")
import managed_backup as core
import backup_scheduler
root = ''' + repr(root) + '''
pathlib.Path(root).mkdir(mode=0o700, exist_ok=True)
store = core.Store(root)
engine = backup_scheduler.Scheduler(store, expected_bytes=1048576)
with store.lock(10):
''' + "\n".join("    " + line for line in body.strip().splitlines())
        if now is not None:
            script = script.replace("with store.lock(10):", "engine.clock = lambda: " + repr(now) + "\nwith store.lock(10):")
        return cls.run_command("run", "--rm", "--no-deps", "-T", "--entrypoint", "python3", "pgbackup", "-c", script)

    def test_scheduler_real_recreate_reuses_persistent_deadline_and_config(self):
        root = "/backups/recreate-" + uuid.uuid4().hex
        self.scheduler_python(root, '''
store.create("scheduled", 1048576)
config = engine.bootstrap()["config"]
config["scheduler"]["enabled"] = True
engine.configure(config, 0)
print(json.dumps(engine.tick()))
''')
        # Run the actual entrypoint, then force-recreate it against the same volume.
        # The root is fixed for each disposable case; no production root is used.
        self.run_command("run", "-d", "--no-deps", "-e", "BACKUP_DIR=" + root,
                         "--name", "fs123-" + root.rsplit("-", 1)[1], "pgbackup")
        container = "fs123-" + root.rsplit("-", 1)[1]
        try:
            for _ in range(2):
                result = self.scheduler_python(root, '''
print(json.dumps(engine.tick()))
print(len(engine.records()))
''')
                self.assertEqual("1", result.stdout.strip().splitlines()[-1])
                subprocess.run(["docker", "restart", container], check=True, capture_output=True, timeout=30)
            # Recreate (not only restart): remove only this disposable test worker.
            subprocess.run(["docker", "rm", "-f", container], check=True, capture_output=True, timeout=30)
            self.run_command("run", "-d", "--no-deps", "-e", "BACKUP_DIR=" + root, "--name", container, "pgbackup")
            result = self.scheduler_python(root, '''
print(json.dumps(engine.tick()))
print(len(engine.records()))
''')
            self.assertEqual("1", result.stdout.strip().splitlines()[-1])
            observation = json.loads(result.stdout.strip().splitlines()[-2])
            self.assertEqual("HEALTHY", observation["health"])
            self.assertEqual(1, observation["applied_revision"])
        finally:
            subprocess.run(["docker", "rm", "-f", container], check=True, capture_output=True, timeout=30)

    def test_overdue_restart_catches_up_once_and_state_loss_reconciles_real_archive(self):
        root = "/backups/catchup-" + uuid.uuid4().hex
        result = self.scheduler_python(root, '''
original = core.utc_now
core.utc_now = lambda: backup_scheduler.stamp(time.time() - 25 * 3600)
store.create("scheduled", 1048576)
core.utc_now = original
config = engine.bootstrap()["config"]
config["scheduler"]["enabled"] = True
engine.configure(config, 0)
print(json.dumps(engine.tick()))
''')
        self.assertEqual("HEALTHY", json.loads(result.stdout.strip().splitlines()[-1])["health"])
        result = self.scheduler_python(root, '''
(engine.folder / "state.json").unlink()
print(json.dumps(engine.tick()))
print(len(engine.records()))
''')
        self.assertEqual("2", result.stdout.strip().splitlines()[-1])
        self.assertEqual("HEALTHY", json.loads(result.stdout.strip().splitlines()[-2])["health"])
        self.scheduler_python(root, '''
(engine.folder / "state.json").write_text("{broken")
print(json.dumps(engine.tick()))
assert len(engine.records()) == 2
assert len(list(engine.folder.glob("*.invalid"))) == 1
''')

    def test_clock_rollback_and_semantic_state_recovery_across_real_worker_restarts(self):
        root = "/backups/clock-state-" + uuid.uuid4().hex
        started = 1795000000.0
        result = self.scheduler_python(root, '''
now = [1795000000.0 - 25 * 3600]
core.utc_now = lambda: backup_scheduler.stamp(now[0])
store.create("scheduled", 1048576)
now[0] = 1795000000.0
engine.clock = lambda: now[0]
config = engine.bootstrap()["config"]
config["scheduler"]["enabled"] = True
engine.configure(config, 0)
original = core._run
def rollback(args, **kwargs):
    result = original(args, **kwargs)
    if args[0] == "pg_dump" and "--version" not in args:
        now[0] -= 48 * 3600
    return result
core._run = rollback
result = engine.tick()
assert result["health"] == "HEALTHY", result
item = engine.present(engine.records())[0]
assert item["created_at_utc"] == item["completed_at_utc"] == backup_scheduler.stamp(1795000000.0)
assert item["completed_observed_at_utc"] == backup_scheduler.stamp(now[0])
print(json.dumps(result))
''')
        original = json.loads(result.stdout.strip().splitlines()[-1])["schedule"]
        # Each invocation is a new container/process using the same persistent root.
        self.scheduler_python(root, '''
assert engine.tick()["health"] == "HEALTHY"
assert len(engine.records()) == 2
path = engine.folder / "state.json"
value = core._read(path)
value["last_scheduled_backup_id"] = "a" * 32
payload = (json.dumps(value, indent=3) + "\\n\\n").encode()
path.write_bytes(payload)
(engine.folder / "expected-invalid-bytes").write_bytes(payload)
''', now=started - 48 * 3600)
        result = self.scheduler_python(root, '''
result = engine.tick()
assert result["health"] == "HEALTHY", result
saved = list(engine.folder.glob("*.invalid"))
assert len(saved) == 1
assert saved[0].read_bytes() == (engine.folder / "expected-invalid-bytes").read_bytes()
assert len(engine.records()) == 2
print(json.dumps(result))
''', now=started - 48 * 3600)
        recovered = json.loads(result.stdout.strip().splitlines()[-1])["schedule"]
        self.assertEqual(original["last_scheduled_backup_id"], recovered["last_scheduled_backup_id"])
        self.assertEqual(original["last_scheduled_success_at_utc"], recovered["last_scheduled_success_at_utc"])
        self.scheduler_python(root, '''
now = 1795000000.0 + 86400
core.utc_now = lambda: backup_scheduler.stamp(now)
result = engine.tick()
assert result["health"] == "HEALTHY", result
assert len(engine.records()) == 3
''', now=started + 86400)
        self.scheduler_python(root, '''
assert engine.tick()["health"] == "HEALTHY"
assert len(engine.records()) == 3
assert len(list(engine.folder.glob("*.invalid"))) == 1
''', now=started + 86400)

    def test_real_dump_failure_persists_retry_and_keeps_validated_archive(self):
        root = "/backups/failure-" + uuid.uuid4().hex
        result = self.scheduler_python(root, '''
import os
original = core.utc_now
core.utc_now = lambda: backup_scheduler.stamp(time.time() - 25 * 3600)
first = store.create("scheduled", 1048576)
core.utc_now = original
config = engine.bootstrap()["config"]
config["scheduler"]["enabled"] = True
engine.configure(config, 0)
os.environ["PGDATABASE"] = "does_not_exist"
result = engine.tick()
assert result["error_code"] == "DUMP_FAILED", result
assert result["schedule"]["last_scheduled_backup_id"] == first["backup_id"]
assert not list(store.jobs.glob("*.json"))
print(json.dumps(result))
''')
        state = json.loads(result.stdout.strip().splitlines()[-1])["schedule"]
        self.assertEqual(1, state["failures"])
        result = self.scheduler_python(root, '''
result = engine.tick()
assert len(engine.records()) == 1
print(json.dumps(result))
''')
        restarted = json.loads(result.stdout.strip().splitlines()[-1])["schedule"]
        self.assertEqual(state["next_retry_at_utc"], restarted["next_retry_at_utc"])
        self.assertEqual(1, restarted["failures"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
