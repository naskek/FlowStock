#!/bin/sh
set -eu

script_dir="$(CDPATH= cd -- "$(dirname "$0")" && pwd)"
migrations_dir="${MIGRATIONS_DIR:-/migrations}"

sh "${script_dir}/wait_for_postgres.sh"

if [ ! -d "$migrations_dir" ]; then
    echo "[migrator] migrations directory not found: $migrations_dir" >&2
    exit 1
fi

psql -v ON_ERROR_STOP=1 <<'SQL'
CREATE TABLE IF NOT EXISTS schema_migrations (
    version TEXT PRIMARY KEY,
    filename TEXT NOT NULL UNIQUE,
    applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
SQL

find "$migrations_dir" -maxdepth 1 -type f -name 'V*.sql' | sort | while IFS= read -r migration_path; do
    filename="$(basename "$migration_path")"
    version="${filename%%__*}"
    if [ "$version" = "$filename" ]; then
        echo "[migrator] invalid migration filename: $filename" >&2
        exit 1
    fi

    applied_filename="$(psql -At -v ON_ERROR_STOP=1 \
        -v migration_version="$version" \
        -c "SELECT filename FROM schema_migrations WHERE version = :'migration_version' LIMIT 1;")"
    if [ -n "$applied_filename" ]; then
        if [ "$applied_filename" != "$filename" ]; then
            echo "[migrator] migration version collision: $version is recorded as $applied_filename but tracked migration is $filename" >&2
            exit 1
        fi

        echo "[migrator] skip $filename"
        continue
    fi

    echo "[migrator] apply $filename"
    psql -v ON_ERROR_STOP=1 \
        -v migration_version="$version" \
        -v migration_filename="$filename" <<SQL
BEGIN;
\i $migration_path
INSERT INTO schema_migrations(version, filename, applied_at)
VALUES (:'migration_version', :'migration_filename', NOW());
COMMIT;
SQL
done

echo "[migrator] migrations complete"
