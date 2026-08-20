#!/usr/bin/env bash
set -euo pipefail

script_dir="$(CDPATH= cd -- "$(dirname "$0")" && pwd)"
. "${script_dir}/common.sh"

ensure_docker

report="$(compose exec -T postgres sh -lc 'psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At' <<'SQL'
SELECT 'BLOCKING|BLANK_UOM|' || u.id::text || '|items=' ||
       COALESCE(STRING_AGG(i.id::text, ',' ORDER BY i.id), 'none')
FROM uoms u
LEFT JOIN items i ON LOWER(BTRIM(i.base_uom)) = LOWER(BTRIM(u.name))
WHERE BTRIM(u.name) = ''
GROUP BY u.id
UNION ALL
SELECT 'BLOCKING|DUPLICATE|' || LOWER(BTRIM(name)) || '|uoms=' || STRING_AGG(id::text, ',' ORDER BY id)
FROM uoms
GROUP BY LOWER(BTRIM(name))
HAVING COUNT(*) > 1
UNION ALL
SELECT 'BLOCKING|BLANK_ITEM|' || id::text
FROM items
WHERE BTRIM(base_uom) = ''
UNION ALL
SELECT 'BLOCKING|ORPHAN|' || LOWER(BTRIM(i.base_uom)) || '|items=' || STRING_AGG(i.id::text, ',' ORDER BY i.id)
FROM items i
WHERE BTRIM(i.base_uom) <> ''
  AND LOWER(BTRIM(i.base_uom)) <> 'шт'
  AND NOT EXISTS (
      SELECT 1 FROM uoms u
      WHERE LOWER(BTRIM(u.name)) = LOWER(BTRIM(i.base_uom)))
GROUP BY LOWER(BTRIM(i.base_uom))
UNION ALL
SELECT 'ALLOWED|LEGACY_ШТ|items=' || STRING_AGG(id::text, ',' ORDER BY id)
FROM items
WHERE LOWER(BTRIM(base_uom)) = 'шт'
HAVING COUNT(*) > 0
UNION ALL
SELECT 'WARNING|MASTER_ШТ_OVERLAP|' || id::text || '|' || name
FROM uoms
WHERE LOWER(BTRIM(name)) = 'шт'
ORDER BY 1;
SQL
)"

if [ -n "$report" ]; then
    echo "$report"
fi

if printf '%s\n' "$report" | grep -q '^BLOCKING|'; then
    echo "[uom-references] blocking findings detected; run uom-remediation after a fresh backup before V0035." >&2
    exit 1
fi

echo "[uom-references] preflight passed; legacy шт references remain allowed"
