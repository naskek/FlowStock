#!/usr/bin/env python3
"""Worker wiring and maintenance regressions; no database required."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

BACKUP = Path(__file__).resolve().parents[1]
REPO = BACKUP.parents[1]
WORKER = BACKUP / "pg_backup.sh"
DOCKERFILE = BACKUP / "Dockerfile"
COMPOSE = REPO / "deploy" / "docker-compose.yml"


class ManagedWorkerTests(unittest.TestCase):
    def test_image_and_volume_wiring(self):
        dockerfile = DOCKERFILE.read_text()
        compose = COMPOSE.read_text()
        self.assertIn("FROM postgres:16-alpine", dockerfile)
        self.assertIn("apk add --no-cache python3", dockerfile)
        self.assertIn("COPY deploy/backup/managed_backup.py", dockerfile)
        worker = compose.split("\n  pgbackup:\n", 1)[1].split("\nvolumes:", 1)[0]
        self.assertIn("dockerfile: deploy/backup/Dockerfile", worker)
        self.assertIn("pgbackups:/backups", worker)
        self.assertIn("BACKUP_EXPECTED_BYTES:", worker)
        self.assertIn("migrator:", worker)
        self.assertIn("condition: service_completed_successfully", worker)
        migrator = compose.split("\n  migrator:\n", 1)[1].split("\n  flowstock:\n", 1)[0]
        self.assertNotIn("\n      migrator:", migrator)
        self.assertNotIn("image: postgres:16-alpine", worker)

    def test_legacy_retention_never_deletes_existing_archives(self):
        source = WORKER.read_text()
        self.assertIn('exec python3 "$SCHEDULER"', source)
        self.assertNotIn("rm -f", source)
        self.assertNotIn("BACKUP_KEEP", source)
        self.assertNotIn("BACKUP_INTERVAL_SECONDS", source)
        self.assertNotIn("pg_dump -Fc", source)

    def _run_worker(self, *, scheduler_failure=False, check_runtime=False):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "backups").mkdir()
            fake_bin = root / "bin"
            fake_bin.mkdir()
            log = root / "requests"
            python = fake_bin / "python3"
            python.write_text('''#!/bin/sh
case " $* " in
  *backup_scheduler.py*" --help "*)
    if [ "$FAKE_SCHEDULER_FAILURE" = 1 ]; then exit 1; fi
    exit 0;;
  *" --help "*) exit 0;;
  *" run "*) printf '%s\\n' "$*" >> "$FAKE_LOG";;
  *) exit 44;;
esac
''')
            python.chmod(0o755)
            for client in ("pg_dump", "pg_restore"):
                path = fake_bin / client
                path.write_text('#!/bin/sh\necho "' + client + ' (PostgreSQL) 16.0"\n')
                path.chmod(0o755)
            worker = root / "worker.sh"
            worker.write_text(WORKER.read_text().replace(
                'MANAGER="/usr/local/bin/managed_backup.py"',
                'MANAGER="' + str(BACKUP / "managed_backup.py") + '"').replace(
                'SCHEDULER="/usr/local/bin/backup_scheduler.py"',
                'SCHEDULER="' + str(BACKUP / "backup_scheduler.py") + '"'))
            env = dict(os.environ, PATH=str(fake_bin) + os.pathsep + os.environ["PATH"],
                       FAKE_SCHEDULER_FAILURE="1" if scheduler_failure else "0",
                       FAKE_LOG=str(log), BACKUP_DIR=str(root / "backups"),
                       BACKUP_EXPECTED_BYTES="4096")
            result = subprocess.run(["sh", str(worker), *(["--check-runtime"] if check_runtime else [])],
                                    env=env, capture_output=True, text=True, timeout=5)
            return (log.read_text().splitlines() if log.exists() else []), result

    def test_entrypoint_delegates_to_single_durable_scheduler(self):
        calls, result = self._run_worker()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(1, len(calls))
        self.assertIn("--expected-bytes 4096 run", calls[0])
        self.assertIn("--lock-timeout 10", calls[0])

    def test_runtime_check_has_no_scheduler_side_effects(self):
        calls, result = self._run_worker(check_runtime=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual([], calls)

    def test_missing_or_broken_scheduler_is_a_permanent_runtime_failure(self):
        calls, result = self._run_worker(scheduler_failure=True)
        self.assertEqual(78, result.returncode)
        self.assertEqual([], calls)
        self.assertIn("backup_scheduler.py cannot execute", result.stderr)


if __name__ == "__main__":
    unittest.main()
