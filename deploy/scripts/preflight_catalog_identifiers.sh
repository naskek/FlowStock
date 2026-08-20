#!/usr/bin/env bash
set -euo pipefail

script_dir="$(CDPATH= cd -- "$(dirname "$0")" && pwd)"
. "${script_dir}/common.sh"

ensure_docker

duplicates="$(compose exec -T postgres sh -lc 'psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At' <<'SQL'
SELECT 'BARCODE|' || LOWER(BTRIM(barcode)) || '|' || STRING_AGG(id::text, ',' ORDER BY id)
FROM items
WHERE NULLIF(BTRIM(barcode), '') IS NOT NULL
GROUP BY LOWER(BTRIM(barcode))
HAVING COUNT(*) > 1
UNION ALL
SELECT 'GTIN|' || LOWER(BTRIM(gtin)) || '|' || STRING_AGG(id::text, ',' ORDER BY id)
FROM items
WHERE NULLIF(BTRIM(gtin), '') IS NOT NULL
GROUP BY LOWER(BTRIM(gtin))
HAVING COUNT(*) > 1
ORDER BY 1;
SQL
)"

if [ -n "$duplicates" ]; then
    echo "[catalog-identifiers] case-insensitive duplicates found:" >&2
    echo "$duplicates" >&2
    echo "[catalog-identifiers] resolve through FlowStock WPF/API after a fresh backup; no data was changed." >&2
    exit 1
fi

echo "[catalog-identifiers] preflight passed; no case-insensitive barcode/GTIN duplicates"
