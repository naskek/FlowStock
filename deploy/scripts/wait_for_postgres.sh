#!/bin/sh
set -eu

timeout_seconds="${POSTGRES_WAIT_TIMEOUT_SECONDS:-120}"
interval_seconds="${POSTGRES_WAIT_INTERVAL_SECONDS:-2}"
elapsed=0

echo "[migrator] waiting for postgres at ${PGHOST:?}:${PGPORT:-5432}/${PGDATABASE:?}"

while ! pg_isready -h "${PGHOST:?}" -p "${PGPORT:-5432}" -U "${PGUSER:?}" -d "${PGDATABASE:?}" >/dev/null 2>&1; do
    elapsed=$((elapsed + interval_seconds))
    if [ "$elapsed" -ge "$timeout_seconds" ]; then
        echo "[migrator] postgres did not become ready within ${timeout_seconds}s" >&2
        exit 1
    fi

    sleep "$interval_seconds"
done

echo "[migrator] postgres is ready"
