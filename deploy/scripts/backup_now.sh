#!/usr/bin/env bash
set -euo pipefail

source "$(cd -- "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

ensure_docker
ensure_compose_config
# Never start PostgreSQL during an active destructive restore.
if [[ -e "${FLOWSTOCK_RUNTIME_DIR}/restore/active.json" || -L "${FLOWSTOCK_RUNTIME_DIR}/restore/active.json" ]]; then
    fail "restore maintenance active; operator backup denied"
fi
ensure_postgres_healthy

target_path="$(resolve_backup_path "${1:-}")"
create_backup "$target_path"

log "backup saved to $target_path"
printf '%s\n' "$target_path"
