#!/usr/bin/env python3
"""Real restore entrypoint on a disposable, port-free PostgreSQL 16 Compose stack."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from restore_postgres import DockerPostgres, Restore, RestoreError, BackupError

SCRIPT = Path(__file__).resolve().parents[1] / "restore_dump.sh"


class PostgreSQLRestoreTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix="flowstock-restore-pg16-")
        cls.base = Path(cls.temp.name)
        cls.envfile = cls.base / "test.env"
        cls.envfile.write_text("")
        cls.composefile = cls.base / "compose.yml"
        dummy = '''
    image: postgres:16-alpine
    entrypoint: ["sh", "-c", "exec sleep infinity"]
    restart: "no"
'''
        cls.composefile.write_text('''services:
  postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: restore_fixture
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD: isolated_ci_only
    tmpfs: [/var/lib/postgresql/data]
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres -d restore_fixture"]
      interval: 1s
      timeout: 3s
      retries: 60
    restart: "no"
  flowstock:
''' + dummy + '  nginx:\n' + dummy + '  discovery-relay:\n' + dummy + '  pgbackup:\n' + dummy + '''networks:
  default:
    internal: true
''')
        os.environ.update(FLOWSTOCK_PROJECT_NAME="flowstock-restore-test-" + uuid.uuid4().hex[:10],
                          FLOWSTOCK_ENV_FILE=str(cls.envfile), FLOWSTOCK_COMPOSE_FILE=str(cls.composefile))
        cls.backend = DockerPostgres()
        try:
            cls.backend.run([*cls.backend.compose, "up", "-d", "--wait"])
            cls.backend.preflight()
        except BaseException:
            cls.backend.run([*cls.backend.compose, "down", "--volumes", "--remove-orphans"])
            raise

    @classmethod
    def tearDownClass(cls):
        cls.backend.run([*cls.backend.compose, "down", "--volumes", "--remove-orphans"])
        cls.temp.cleanup()

    def setUp(self):
        self.case = self.base / uuid.uuid4().hex
        self.case.mkdir(mode=0o700)
        self.backend.diagnostics = self.case / "test-diagnostics.log"
        self.backend.diagnostics.touch(mode=0o600)
        os.environ.update(FLOWSTOCK_RUNTIME_DIR=str(self.case / "runtime"),
                          FLOWSTOCK_BACKUP_OUTPUT_DIR=str(self.case / "backups"))
        os.environ.pop("FLOWSTOCK_PRE_RESTORE_BACKUP_PATH_OVERRIDE", None)
        self.backend.sql('DROP SCHEMA public CASCADE; CREATE SCHEMA public;', database="restore_fixture")
        self.backend.sql('''CREATE TABLE records(id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            value text NOT NULL CHECK(length(value)>0));
            CREATE TABLE refs(id int PRIMARY KEY, record_id bigint REFERENCES records(id));
            CREATE TABLE schema_migrations(version int PRIMARY KEY, filename text NOT NULL);
            INSERT INTO schema_migrations VALUES(1,'fixture.sql');
            INSERT INTO records(value) SELECT md5(g::text) FROM generate_series(1,5000) g;
            INSERT INTO refs VALUES(1,1);
            CREATE TABLE payload(value text NOT NULL);
            INSERT INTO payload SELECT string_agg(md5(g::text),'') FROM generate_series(1,500000) g;
            ''', database="restore_fixture")
        self.source = self.case / "legacy.dump"
        self.backend.backup(self.source)
        self.assertGreater(self.source.stat().st_size, 4 * 1024 * 1024)
        self.backend.run([*self.backend.compose, "up", "-d", *self.backend.stopped_services])

    def entrypoint(self, *args, expect_success=True):
        process = subprocess.run(["bash", str(SCRIPT), *map(str, args)], capture_output=True, text=True)
        if expect_success and process.returncode != 0:
            raise AssertionError(process.stdout + process.stderr)
        if not expect_success:
            self.assertNotEqual(process.returncode, 0, process.stdout)
        return process

    def active(self):
        root = Path(os.environ["FLOWSTOCK_RUNTIME_DIR"]) / "restore"
        active = json.loads((root / "active.json").read_text())
        operation = root / active["operation_id"]
        return json.loads(sorted(operation.glob("[0-9]*.json"))[-1].read_text())

    def verify_data(self, expected=5000):
        self.assertEqual(self.backend.sql("SELECT count(*) FROM records", database="restore_fixture"), str(expected))
        self.assertEqual(self.backend.sql("SELECT count(*) FROM refs", database="restore_fixture"), "1")
        self.assertEqual(self.backend.sql("SELECT filename FROM schema_migrations", database="restore_fixture"), "fixture.sql")
        self.assertEqual(self.backend.sql("SELECT length(value) FROM payload", database="restore_fixture"), "16000000")
        self.backend.sql('''DO $$ BEGIN
            BEGIN INSERT INTO refs VALUES(2,9999999); RAISE EXCEPTION 'missing FK';
            EXCEPTION WHEN foreign_key_violation THEN NULL; END;
            BEGIN INSERT INTO records(id,value) OVERRIDING SYSTEM VALUE VALUES(1,'dup');
            RAISE EXCEPTION 'missing PK'; EXCEPTION WHEN unique_violation THEN NULL; END;
            BEGIN INSERT INTO records(value) VALUES(''); RAISE EXCEPTION 'missing CHECK';
            EXCEPTION WHEN check_violation THEN NULL; END;
            IF nextval(pg_get_serial_sequence('records','id')) <= 5000 THEN
              RAISE EXCEPTION 'sequence not restored'; END IF;
            END $$;''', database="restore_fixture")

    def test_success_legacy_dump_rows_constraints_sequences_and_operator_completion(self):
        # Fresh guard must include a write newer than the input archive.
        self.backend.sql("INSERT INTO records(value) VALUES('newer-than-input')", database="restore_fixture")
        self.entrypoint(self.source)
        self.verify_data()
        state = self.active()
        self.assertEqual(state["phase"], "RESTORED_AWAITING_VALIDATION")
        self.backend.assert_stopped()
        self.entrypoint(self.source, expect_success=False)
        self.entrypoint("--recover", state["operation_id"])
        self.verify_data(5001)
        recovered = self.active()
        self.entrypoint("--complete", recovered["operation_id"], "--confirm-validated")
        self.backend.assert_stopped()
        self.assertTrue(Path(state["safety_path"]).exists())

    def test_invalid_header_and_corrupt_payload_fail_before_service_stop(self):
        content = self.source.read_bytes()
        # TOC alone is readable, but the deliberately truncated data payload is not.
        truncated = self.case / "truncated.dump"
        truncated.write_bytes(content[:-100])
        truncated.chmod(0o600)
        self.backend.pg("sh", "-c", 'status=0; pg_restore --list >/dev/null || status=$?; '
                        'cat >/dev/null || exit 1; exit "$status"', source=truncated, output=subprocess.DEVNULL)
        with self.assertRaises(RestoreError):
            self.backend.validate(truncated)
        for name, data in (("header", b"bad header"), ("empty", b""), ("payload", truncated.read_bytes())):
            archive = self.case / (name + ".dump")
            archive.write_bytes(data)
            self.entrypoint(archive, expect_success=False)
            running = self.backend.run([*self.backend.compose, "ps", "--status", "running", "--services"]).splitlines()
            self.assertIn("flowstock", running)
            self.assertIn("pgbackup", running)
            self.verify_data()

    def test_real_sql_error_rolls_back_transaction_and_explicit_recovery(self):
        # Preserve the archive structure/payload while introducing a genuine SQL syntax error.
        content = self.source.read_bytes()
        self.assertIn(b"CREATE TABLE public.records", content)
        broken = self.case / "sql-error.dump"
        broken.write_bytes(content.replace(b"CREATE TABLE public.records", b"CREATX TABLE public.records", 1))
        broken.chmod(0o600)
        self.backend.validate(broken)  # TOC/payload parsing cannot prove SQL validity.
        self.entrypoint(broken, expect_success=False)
        state = self.active()
        self.assertEqual(state["phase"], "RESTORING")
        self.assertTrue(state["safety_validated"])
        self.backend.assert_stopped()
        self.assertEqual(self.backend.sql("SELECT count(*) FROM pg_tables WHERE schemaname='public'",
                                          database="restore_fixture"), "0")
        self.entrypoint("--complete", state["operation_id"], "--confirm-validated", expect_success=False)
        self.entrypoint("--recover", state["operation_id"])
        self.verify_data()
        self.backend.assert_stopped()

    def test_invalid_safety_blocks_drop_with_real_database_unchanged(self):
        class InvalidSafety(DockerPostgres):
            def backup(self, path):
                path.write_bytes(b"PGDMPinvalid-safety")
                path.chmod(0o600)
        backend = InvalidSafety()
        restore = Restore(backend, self.case / "fault-runtime")
        with restore.lock(), self.assertRaises(RestoreError):
            restore.execute(self.source)
        self.verify_data()
        self.assertFalse(restore.state["safety_validated"])
        self.assertFalse(restore.state["destructive_started"])
        backend.assert_stopped()


    def test_drop_and_create_boundary_failures_require_explicit_operator_preparation(self):
        for boundary in ("connections_disabled", "database_dropped"):
            with self.subTest(boundary=boundary):
                class BoundaryFault(DockerPostgres):
                    def recreate(self):
                        self.sql('ALTER DATABASE "restore_fixture" ALLOW_CONNECTIONS false;')
                        if boundary == "database_dropped":
                            self.sql('DROP DATABASE "restore_fixture" WITH (FORCE);')
                        raise RestoreError("injected DDL boundary failure")
                runtime = self.case / boundary
                os.environ["FLOWSTOCK_RUNTIME_DIR"] = str(runtime)
                backend = BoundaryFault()
                restore = Restore(backend, runtime / "restore")
                with restore.lock(), self.assertRaises(RestoreError):
                    restore.execute(self.source)
                state = restore.state.copy()
                self.assertTrue(state["safety_validated"])
                self.entrypoint("--recover", state["operation_id"], expect_success=False)
                self.assertEqual(self.active()["operation_id"], state["operation_id"])
                if boundary == "database_dropped":
                    self.backend.sql('CREATE DATABASE "restore_fixture" TEMPLATE template0;')
                else:
                    self.backend.sql('ALTER DATABASE "restore_fixture" ALLOW_CONNECTIONS true;')
                self.entrypoint("--recover", state["operation_id"])
                self.verify_data()
                self.backend.assert_stopped()
                self.assertTrue(Path(state["safety_path"]).exists())


if __name__ == "__main__":
    unittest.main(verbosity=2)
