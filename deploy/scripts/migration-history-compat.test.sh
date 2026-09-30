#!/bin/sh
set -eu

script_dir="$(CDPATH= cd -- "$(dirname "$0")" && pwd)"
migrations_dir="${MIGRATIONS_DIR:-/migrations}"
base_db="${PGDATABASE:?}"
legacy_db="${base_db}_legacy_v0036"
collision_db="${base_db}_version_collision"

cleanup() {
    PGDATABASE=postgres dropdb --if-exists --force "$legacy_db" >/dev/null 2>&1 || true
    PGDATABASE=postgres dropdb --if-exists --force "$collision_db" >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

create_db() {
    db_name="$1"
    PGDATABASE=postgres dropdb --if-exists --force "$db_name" >/dev/null 2>&1 || true
    PGDATABASE=postgres createdb --owner="${PGUSER:?}" "$db_name"
}

if [ -e "$migrations_dir/V0036__order_coverage_transfer_provenance.sql" ]; then
    echo "[migration-compat] historical V0036 number must not be reused by provenance migration" >&2
    exit 1
fi

if [ ! -f "$migrations_dir/V0043__order_coverage_transfer_provenance.sql" ]; then
    echo "[migration-compat] V0043 provenance migration is missing" >&2
    exit 1
fi

echo "[migration-compat] verify historical production V0036 can coexist with current chain"
create_db "$legacy_db"

PGDATABASE="$legacy_db" psql -v ON_ERROR_STOP=1 <<'SQL'
CREATE TABLE schema_migrations (
    version TEXT PRIMARY KEY,
    filename TEXT NOT NULL UNIQUE,
    applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
INSERT INTO schema_migrations(version, filename)
VALUES ('V0036', 'V0036__aggregate_marking_subjects_and_ready_hu.sql');
SQL

PGDATABASE="$legacy_db" sh "$script_dir/run_migrations.sh"

legacy_history="$(PGDATABASE="$legacy_db" psql -At -v ON_ERROR_STOP=1 -c "
SELECT version || '|' || filename
FROM schema_migrations
WHERE version IN ('V0036', 'V0043')
ORDER BY version;
")"
expected_history="$(printf '%s\n%s' \
    'V0036|V0036__aggregate_marking_subjects_and_ready_hu.sql' \
    'V0043|V0043__order_coverage_transfer_provenance.sql')"

if [ "$legacy_history" != "$expected_history" ]; then
    echo "[migration-compat] historical V0036 / current V0043 history mismatch" >&2
    printf 'expected:\n%s\nactual:\n%s\n' "$expected_history" "$legacy_history" >&2
    exit 1
fi

coverage_tables="$(PGDATABASE="$legacy_db" psql -At -v ON_ERROR_STOP=1 -c "
SELECT
    to_regclass('public.order_coverage_transfers')::text || '|' ||
    to_regclass('public.order_coverage_transfer_lines')::text;
")"

if [ "$coverage_tables" != "order_coverage_transfers|order_coverage_transfer_lines" ]; then
    echo "[migration-compat] V0043 provenance tables were not created" >&2
    exit 1
fi

echo "[migration-compat] verify same-version/different-filename collision is rejected"
create_db "$collision_db"

PGDATABASE="$collision_db" psql -v ON_ERROR_STOP=1 <<'SQL'
CREATE TABLE schema_migrations (
    version TEXT PRIMARY KEY,
    filename TEXT NOT NULL UNIQUE,
    applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
INSERT INTO schema_migrations(version, filename)
VALUES ('V0001', 'V0001__unexpected_historical_name.sql');
SQL

set +e
collision_output="$(PGDATABASE="$collision_db" sh "$script_dir/run_migrations.sh" 2>&1)"
collision_status=$?
set -e

if [ "$collision_status" -eq 0 ]; then
    echo "[migration-compat] migrator accepted a same-version/different-filename collision" >&2
    exit 1
fi

expected_collision="[migrator] migration version collision: V0001 is recorded as V0001__unexpected_historical_name.sql but tracked migration is V0001__baseline.sql"
if ! printf '%s\n' "$collision_output" | grep -F "$expected_collision" >/dev/null; then
    echo "[migration-compat] migrator failed for the wrong reason" >&2
    printf '%s\n' "$collision_output" >&2
    exit 1
fi

echo "[migration-compat] migration history compatibility tests passed"
