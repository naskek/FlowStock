#!/usr/bin/env bash
set -euo pipefail

source "$(cd -- "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

usage() {
    cat <<EOF
Usage:
  bash deploy/scripts/backfill_order_reservations.sh
  bash deploy/scripts/backfill_order_reservations.sh --apply

Default mode is dry-run. Run a fresh backup before --apply.
EOF
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    usage
    exit 0
fi

ensure_docker
ensure_compose_config
ensure_postgres_healthy
run_migrator

log "building flowstock maintenance image"
compose build flowstock

log "running HU reservation backfill (${1:---dry-run})"
compose run --rm --no-deps flowstock maintenance backfill-reservations "$@"
