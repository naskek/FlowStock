#!/usr/bin/env python3
"""Worker wiring and maintenance regressions; no database required."""
import os
from pathlib import Path
import subprocess
import tempfile
import time
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
        self.assertNotIn("image: postgres:16-alpine", worker)

    def test_legacy_retention_never_deletes_existing_archives(self):
        source = WORKER.read_text()
        self.assertIn("--origin scheduled", source)
        self.assertIn("--lock-timeout", source)
        self.assertNotIn("rm -f", source)
        self.assertNotIn("BACKUP_KEEP", source)
        self.assertNotIn("pg_dump -Fc", source)

    def _run_worker(self, *, paused=False, status_failure=False):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "backups").mkdir()
            fake_bin = root / "bin"
            fake_bin.mkdir()
            log = root / "requests"
            python = fake_bin / "python3"
            python.write_text("""#!/bin/sh
case " $* " in
  *" status "*)
    if [ "$FAKE_STATUS_FAILURE" = 1 ]; then exit 1; fi
    printf '{"paused": %s}\\n' "$FAKE_PAUSED";;
  *" create "*) printf 'created\\n' >> "$FAKE_LOG";;
  *) exit 44;;
esac
""")
            python.chmod(0o755)
            env = dict(os.environ, PATH=str(fake_bin) + os.pathsep + os.environ["PATH"],
                       FAKE_PAUSED="true" if paused else "false",
                       FAKE_STATUS_FAILURE="1" if status_failure else "0",
                       FAKE_LOG=str(log), BACKUP_DIR=str(root / "backups"),
                       BACKUP_INTERVAL_SECONDS="2", BACKUP_EXPECTED_BYTES="4096")
            proc = subprocess.Popen(["sh", str(WORKER)], env=env,
                                    stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            try:
                time.sleep(0.5)
            finally:
                proc.terminate()
                out, err = proc.communicate(timeout=5)
            return (log.read_text().splitlines() if log.exists() else []), out + err

    def test_scheduled_create_when_active(self):
        calls, output = self._run_worker()
        self.assertEqual(calls, ["created"], output)

    def test_paused_never_schedules(self):
        calls, output = self._run_worker(paused=True)
        self.assertEqual(calls, [])
        self.assertIn("maintenance paused", output)

    def test_uncertain_maintenance_state_never_schedules(self):
        calls, output = self._run_worker(status_failure=True)
        self.assertEqual(calls, [])
        self.assertIn("refusing unsafe dump", output)


if __name__ == "__main__":
    unittest.main()
