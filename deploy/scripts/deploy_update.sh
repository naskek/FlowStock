#!/usr/bin/env bash
set -euo pipefail

source "$(cd -- "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

backup_path=""

on_error() {
    local exit_code=$?
    log "deployment failed"
    if [[ -n "$backup_path" ]]; then
        log "latest pre-deploy backup: $backup_path"
    fi
    compose ps || true
    compose logs --tail=80 migrator flowstock nginx postgres || true
    exit "$exit_code"
}

trap on_error ERR

ensure_docker
ensure_compose_config
ensure_postgres_healthy

backup_path="$(resolve_backup_path)"
create_backup "$backup_path"

log "pulling base images"
compose pull postgres nginx pgbackup

log "building flowstock image"
compose build flowstock

log "recreating postgres if image changed"
compose up -d postgres
wait_for_service_status postgres healthy "$POSTGRES_WAIT_TIMEOUT_SECONDS"

run_migrator

log "starting application containers"
compose up -d --force-recreate flowstock nginx pgbackup
wait_for_flowstock_ready
wait_for_service_status nginx running 60

log "deployment completed successfully"
log "pre-deploy backup: $backup_path"
