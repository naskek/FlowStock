#!/usr/bin/env bash
set -euo pipefail

source "$(cd -- "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

ensure_docker
ensure_compose_config
ensure_postgres_healthy

target_path="$(resolve_backup_path "${1:-}")"
create_backup "$target_path"

log "backup saved to $target_path"
printf '%s\n' "$target_path"
