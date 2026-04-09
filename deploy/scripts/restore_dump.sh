#!/usr/bin/env bash
set -euo pipefail

source "$(cd -- "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

dump_path="${1:-}"
[[ -n "$dump_path" ]] || fail "usage: restore_dump.sh /path/to/backup.dump"
[[ -f "$dump_path" ]] || fail "dump file not found: $dump_path"

ensure_docker
ensure_compose_config
ensure_postgres_healthy

pre_restore_backup="$(resolve_backup_path "${FLOWSTOCK_BACKUP_OUTPUT_DIR}/pre_restore/")"
create_backup "$pre_restore_backup"

log "stopping application containers before restore"
compose stop flowstock nginx pgbackup || true

log "dropping active connections and recreating database"
compose exec -T postgres sh -eu -c '
target_db="$POSTGRES_DB"
psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d postgres \
  -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '\''$target_db'\'' AND pid <> pg_backend_pid();"
psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d postgres \
  -c "DROP DATABASE IF EXISTS \"$target_db\";"
psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d postgres \
  -c "CREATE DATABASE \"$target_db\";"
'

log "restoring dump from $dump_path"
cat "$dump_path" | compose exec -T postgres sh -eu -c 'pg_restore --clean --if-exists --no-owner --no-privileges -U "$POSTGRES_USER" -d "$POSTGRES_DB"'

compose up -d pgbackup

log "restore completed"
log "pre-restore backup: $pre_restore_backup"
log "flowstock/nginx were left stopped intentionally; start the expected app revision after validation"
