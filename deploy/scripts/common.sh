#!/usr/bin/env bash

SCRIPT_DIR="$(cd -- "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_DIR="$(cd -- "${SCRIPT_DIR}/.." && pwd)"
FLOWSTOCK_COMPOSE_FILE="${FLOWSTOCK_COMPOSE_FILE:-${DEPLOY_DIR}/docker-compose.yml}"
FLOWSTOCK_ENV_FILE="${FLOWSTOCK_ENV_FILE:-${DEPLOY_DIR}/.env}"
FLOWSTOCK_PROJECT_NAME="${FLOWSTOCK_PROJECT_NAME:-flowstock}"
FLOWSTOCK_RUNTIME_DIR="${FLOWSTOCK_RUNTIME_DIR:-${DEPLOY_DIR}/runtime}"
FLOWSTOCK_BACKUP_OUTPUT_DIR="${FLOWSTOCK_BACKUP_OUTPUT_DIR:-${FLOWSTOCK_RUNTIME_DIR}/backups}"
FLOWSTOCK_HEALTH_TIMEOUT_SECONDS="${FLOWSTOCK_HEALTH_TIMEOUT_SECONDS:-120}"
POSTGRES_WAIT_TIMEOUT_SECONDS="${POSTGRES_WAIT_TIMEOUT_SECONDS:-120}"

log() {
    printf '[flowstock] %s\n' "$*"
}

fail() {
    printf '[flowstock] ERROR: %s\n' "$*" >&2
    exit 1
}

require_file() {
    local path="$1"
    [[ -f "$path" ]] || fail "required file not found: $path"
}

ensure_docker() {
    command -v docker >/dev/null 2>&1 || fail "docker is not installed"
    docker compose version >/dev/null 2>&1 || fail "docker compose is not available"
    require_file "$FLOWSTOCK_COMPOSE_FILE"
    require_file "$FLOWSTOCK_ENV_FILE"
}

compose() {
    docker compose \
        --project-name "$FLOWSTOCK_PROJECT_NAME" \
        --env-file "$FLOWSTOCK_ENV_FILE" \
        -f "$FLOWSTOCK_COMPOSE_FILE" \
        "$@"
}

service_container_id() {
    compose ps -q "$1"
}

service_status() {
    local service="$1"
    local container_id
    container_id="$(service_container_id "$service")"
    if [[ -z "$container_id" ]]; then
        return 1
    fi

    docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$container_id"
}

wait_for_service_status() {
    local service="$1"
    local expected="$2"
    local timeout="${3:-120}"
    local started_at
    local current_status

    started_at="$(date +%s)"
    while true; do
        current_status="$(service_status "$service" 2>/dev/null || true)"
        if [[ "$current_status" == "$expected" ]]; then
            return 0
        fi

        if [[ "$current_status" == "exited" && "$expected" != "exited" ]]; then
            compose logs --tail=50 "$service" >&2 || true
            fail "service '$service' exited before reaching status '$expected'"
        fi

        if (( "$(date +%s)" - started_at >= timeout )); then
            compose logs --tail=50 "$service" >&2 || true
            fail "timeout waiting for service '$service' to become '$expected' (last status: ${current_status:-unknown})"
        fi

        sleep 2
    done
}

ensure_compose_config() {
    log "validating compose configuration"
    compose config -q
}

ensure_postgres_healthy() {
    log "starting postgres"
    compose up -d postgres
    wait_for_service_status postgres healthy "$POSTGRES_WAIT_TIMEOUT_SECONDS"
}

default_backup_path() {
    mkdir -p "$FLOWSTOCK_BACKUP_OUTPUT_DIR"
    printf '%s/FlowStock_%s.dump\n' \
        "$FLOWSTOCK_BACKUP_OUTPUT_DIR" \
        "$(date -u +%Y%m%dT%H%M%SZ)"
}

resolve_backup_path() {
    local requested="${1:-}"
    if [[ -z "$requested" ]]; then
        default_backup_path
        return 0
    fi

    if [[ -d "$requested" || "$requested" == */ ]]; then
        mkdir -p "$requested"
        printf '%s/FlowStock_%s.dump\n' \
            "${requested%/}" \
            "$(date -u +%Y%m%dT%H%M%SZ)"
        return 0
    fi

    mkdir -p "$(dirname "$requested")"
    printf '%s\n' "$requested"
}

create_backup() {
    local target="$1"
    log "creating backup at $target"
    if ! compose exec -T postgres sh -eu -c 'pg_dump -Fc -U "$POSTGRES_USER" -d "$POSTGRES_DB"' >"$target"; then
        rm -f "$target" || true
        fail "pg_dump failed"
    fi
}

run_migrator() {
    log "running SQL migrations"
    compose up --no-deps --force-recreate --abort-on-container-exit --exit-code-from migrator migrator
}

wait_for_flowstock_ready() {
    wait_for_service_status flowstock healthy "$FLOWSTOCK_HEALTH_TIMEOUT_SECONDS"
}
