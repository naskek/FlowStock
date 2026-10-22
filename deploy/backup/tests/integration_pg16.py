#!/usr/bin/env python3
"""Real PostgreSQL 16 managed-backup → isolated restore contract test (CI only)."""
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path("/tmp/flowstock-managed-backup-ci")
TOOL = Path("/tmp/flowstock-managed-backup.py")
DB = os.environ["PGDATABASE"]
VERIFY_DB = "flowstock_backup_restore_ci"


def run(*cmd, db=None, env=None, check=True):
    proc_env = os.environ.copy()
    if db:
        proc_env["PGDATABASE"] = db
    if env:
        proc_env.update(env)
    result = subprocess.run(cmd, env=proc_env, text=True, capture_output=True)
    if check and result.returncode != 0:
        raise AssertionError(f"command {cmd[0]} failed (exit {result.returncode}): {result.stderr[-1000:]}")
    return result


def sql(query: str, db=None):
    return run("psql", "-X", "-A", "-t", "-v", "ON_ERROR_STOP=1", "-c", query, db=db).stdout.strip()


def main():
    if not run("pg_dump", "--version").stdout.startswith("pg_dump (PostgreSQL) 16."):
        raise AssertionError("PostgreSQL 16 client required")
    sql("""CREATE SCHEMA IF NOT EXISTS backup_fixture;
           CREATE TABLE IF NOT EXISTS backup_fixture.records (
               id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
               value text NOT NULL UNIQUE
           );
           INSERT INTO backup_fixture.records(value)
             VALUES ('alpha'),('beta') ON CONFLICT (value) DO NOTHING;
           CREATE TABLE IF NOT EXISTS backup_fixture.refs (
               id integer PRIMARY KEY,
               record_id integer NOT NULL REFERENCES backup_fixture.records(id)
           );
           INSERT INTO backup_fixture.refs(id,record_id)
             SELECT 1,min(id) FROM backup_fixture.records
             ON CONFLICT (id) DO NOTHING;""")
    if int(sql("SELECT count(*) FROM schema_migrations")) < 1:
        raise AssertionError("migrations not loaded")
    # Verify that a deliberately non-superuser backup role can dump migrated schema.
    sql("""DO $$
        DECLARE schema_name text;
        BEGIN
          IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='flowstock_backup_ro_ci') THEN
            CREATE ROLE flowstock_backup_ro_ci LOGIN PASSWORD 'ci_only_readback_fixture';
          END IF;
          FOR schema_name IN
            SELECT nspname FROM pg_namespace
             WHERE nspname <> 'information_schema'
               AND nspname NOT LIKE 'pg_%'
          LOOP
            EXECUTE format('GRANT USAGE ON SCHEMA %I TO flowstock_backup_ro_ci', schema_name);
            EXECUTE format('GRANT SELECT ON ALL TABLES IN SCHEMA %I TO flowstock_backup_ro_ci', schema_name);
            EXECUTE format('GRANT SELECT ON ALL SEQUENCES IN SCHEMA %I TO flowstock_backup_ro_ci', schema_name);
          END LOOP;
          GRANT CONNECT ON DATABASE flowstock_test TO flowstock_backup_ro_ci;
        END $$;""")
    ROOT.mkdir(mode=0o700, exist_ok=True)
    job = run(sys.executable, str(TOOL), "--root", str(ROOT),
              "create", "--origin", "manual", "--expected-bytes", "1048576",
              env={"PGUSER": "flowstock_backup_ro_ci", "PGPASSWORD": "ci_only_readback_fixture"})
    item = json.loads(job.stdout)
    assert item["local_state"] == "LOCAL_VALID"
    assert item["schema_migrations_state"] == "KNOWN"
    archive = ROOT / "managed" / "archives" / (item["backup_id"] + ".dump")
    assert archive.exists() and archive.stat().st_size > 0
    assert hashlib.sha256(archive.read_bytes()).hexdigest() == item["sha256"]

    # Isolated target has a different name, never the source database.
    if VERIFY_DB == DB:
        raise AssertionError("restore target must not equal source")
    sql(f"CREATE DATABASE {VERIFY_DB}", db="postgres")
    try:
        run("pg_restore", "--exit-on-error", "--no-owner", "--no-privileges",
            "-d", VERIFY_DB, str(archive))
        assert sql("SELECT count(*) FROM backup_fixture.records", db=VERIFY_DB) == "2"
        assert sql("SELECT count(*) FROM backup_fixture.refs", db=VERIFY_DB) == "1"
        assert int(sql("SELECT count(*) FROM schema_migrations", db=VERIFY_DB)) > 0
        sql("""DO $$
            BEGIN
              BEGIN
                INSERT INTO backup_fixture.records(id,value)
                  OVERRIDING SYSTEM VALUE VALUES (1, 'duplicate-pk');
                RAISE EXCEPTION 'PK was not restored';
              EXCEPTION WHEN unique_violation THEN
                NULL;
              END;
              BEGIN
                INSERT INTO backup_fixture.refs(id,record_id) VALUES (2,999999);
                RAISE EXCEPTION 'FK was not restored';
              EXCEPTION WHEN foreign_key_violation THEN
                NULL;
              END;
              IF nextval(pg_get_serial_sequence('backup_fixture.records','id')) <= 2 THEN
                 RAISE EXCEPTION 'sequence was not restored';
              END IF;
            END $$;""", db=VERIFY_DB)
    finally:
        sql(f"DROP DATABASE {VERIFY_DB} WITH (FORCE)", db="postgres")
    print("PostgreSQL 16 isolated dump/restore: rows, PK/FK, sequences and migrations PASS")


if __name__ == "__main__":
    main()
