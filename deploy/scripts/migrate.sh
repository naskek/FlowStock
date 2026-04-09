#!/usr/bin/env bash
set -euo pipefail

source "$(cd -- "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

ensure_docker
ensure_compose_config
ensure_postgres_healthy
run_migrator

log "migrations applied successfully"
