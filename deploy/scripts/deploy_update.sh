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
    compose_logs_existing migrator flowstock discovery-relay nginx postgres pgbackup
    exit "$exit_code"
}

trap on_error ERR

ensure_docker
ensure_tls_assets
ensure_compose_config
discovery_network_preflight
ensure_postgres_healthy

backup_path="$(resolve_backup_path "${FLOWSTOCK_BACKUP_PATH_OVERRIDE:-}")"
create_backup "$backup_path"

log "pulling base images"
compose pull postgres nginx pgbackup

log "building application images"
if service_exists discovery-relay; then
    compose build flowstock discovery-relay
else
    compose build flowstock
fi

log "recreating postgres if image changed"
compose up -d postgres
wait_for_service_status postgres healthy "$POSTGRES_WAIT_TIMEOUT_SECONDS"

run_migrator

remove_discovery_relay_containers
assert_no_discovery_relay_containers
allow_udp_7155_free_or_current_flowstock_publish

log "starting flowstock backend"
compose up -d --no-deps --force-recreate flowstock
wait_for_flowstock_ready

require_udp_port_free 7155
check_discovery_backend

if service_exists discovery-relay; then
    log "starting discovery relay"
    compose up -d --no-deps --force-recreate discovery-relay
    wait_for_service_status discovery-relay healthy "$FLOWSTOCK_HEALTH_TIMEOUT_SECONDS"
fi

log "starting edge and backup containers"
compose up -d --no-deps --force-recreate nginx pgbackup
wait_for_service_status nginx running 60

log "deployment completed successfully"
log "pre-deploy backup: $backup_path"
