#!/usr/bin/env bash

SCRIPT_DIR="$(cd -- "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_DIR="$(cd -- "${SCRIPT_DIR}/.." && pwd)"
FLOWSTOCK_ENV_FILE="${FLOWSTOCK_ENV_FILE:-${DEPLOY_DIR}/.env}"

if [[ -f "$FLOWSTOCK_ENV_FILE" ]]; then
    set -a
    # shellcheck disable=SC1090
    source "$FLOWSTOCK_ENV_FILE"
    set +a
fi

FLOWSTOCK_REPO_DIR="${FLOWSTOCK_REPO_DIR:-$(cd -- "${DEPLOY_DIR}/.." && pwd)}"
FLOWSTOCK_COMPOSE_FILE="${FLOWSTOCK_COMPOSE_FILE:-${DEPLOY_DIR}/docker-compose.yml}"
FLOWSTOCK_PROJECT_NAME="${FLOWSTOCK_PROJECT_NAME:-flowstock}"
FLOWSTOCK_RUNTIME_DIR="${FLOWSTOCK_RUNTIME_DIR:-${DEPLOY_DIR}/runtime}"
FLOWSTOCK_BACKUP_OUTPUT_DIR="${FLOWSTOCK_BACKUP_OUTPUT_DIR:-${FLOWSTOCK_RUNTIME_DIR}/backups}"
FLOWSTOCK_RELEASES_DIR="${FLOWSTOCK_RELEASES_DIR:-${FLOWSTOCK_RUNTIME_DIR}/releases}"
FLOWSTOCK_HEALTH_TIMEOUT_SECONDS="${FLOWSTOCK_HEALTH_TIMEOUT_SECONDS:-120}"
POSTGRES_WAIT_TIMEOUT_SECONDS="${POSTGRES_WAIT_TIMEOUT_SECONDS:-120}"
FLOWSTOCK_GIT_REMOTE="${FLOWSTOCK_GIT_REMOTE:-origin}"
FLOWSTOCK_GIT_BRANCH="${FLOWSTOCK_GIT_BRANCH:-main}"
FLOWSTOCK_DEFAULT_DEPLOY_REF="${FLOWSTOCK_DEFAULT_DEPLOY_REF:-${FLOWSTOCK_GIT_REMOTE}/${FLOWSTOCK_GIT_BRANCH}}"
FLOWSTOCK_TLS_MODE="${FLOWSTOCK_TLS_MODE:-manual}"
FLOWSTOCK_TLS_SERVER_NAME="${FLOWSTOCK_TLS_SERVER_NAME:-flowstock.local}"
FLOWSTOCK_TLS_SANS="${FLOWSTOCK_TLS_SANS:-}"
FLOWSTOCK_CA_DIR="${FLOWSTOCK_CA_DIR:-/opt/flowstock-secrets/ca}"
FLOWSTOCK_CA_COMMON_NAME="${FLOWSTOCK_CA_COMMON_NAME:-FlowStock Local Root CA}"
FLOWSTOCK_CA_CERT_DAYS="${FLOWSTOCK_CA_CERT_DAYS:-3650}"
FLOWSTOCK_SERVER_CERT_DAYS="${FLOWSTOCK_SERVER_CERT_DAYS:-825}"
FLOWSTOCK_SERVER_CERT_RENEW_BEFORE_DAYS="${FLOWSTOCK_SERVER_CERT_RENEW_BEFORE_DAYS:-30}"
FLOWSTOCK_TLS_CERT_DIR="${FLOWSTOCK_TLS_CERT_DIR:-${DEPLOY_DIR}/nginx/certs}"
FLOWSTOCK_TLS_CERT_PATH="${FLOWSTOCK_TLS_CERT_PATH:-${FLOWSTOCK_TLS_CERT_DIR}/flowstock.crt}"
FLOWSTOCK_TLS_KEY_PATH="${FLOWSTOCK_TLS_KEY_PATH:-${FLOWSTOCK_TLS_CERT_DIR}/flowstock.key}"
FLOWSTOCK_CA_CERT_PATH="${FLOWSTOCK_CA_CERT_PATH:-${FLOWSTOCK_CA_DIR}/flowstock-root-ca.crt}"
FLOWSTOCK_CA_KEY_PATH="${FLOWSTOCK_CA_KEY_PATH:-${FLOWSTOCK_CA_DIR}/flowstock-root-ca.key}"
FLOWSTOCK_CA_SERIAL_PATH="${FLOWSTOCK_CA_SERIAL_PATH:-${FLOWSTOCK_CA_DIR}/flowstock-root-ca.srl}"

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

require_command() {
    local name="$1"
    command -v "$name" >/dev/null 2>&1 || fail "required command is not installed: $name"
}

ensure_runtime_dirs() {
    mkdir -p "$FLOWSTOCK_RUNTIME_DIR" "$FLOWSTOCK_BACKUP_OUTPUT_DIR" "$FLOWSTOCK_RELEASES_DIR"
}

ensure_docker() {
    command -v docker >/dev/null 2>&1 || fail "docker is not installed"
    docker compose version >/dev/null 2>&1 || fail "docker compose is not available"
    require_file "$FLOWSTOCK_COMPOSE_FILE"
    require_file "$FLOWSTOCK_ENV_FILE"
    ensure_runtime_dirs
}

git_in_repo() {
    git -C "$FLOWSTOCK_REPO_DIR" "$@"
}

ensure_git_repo() {
    git_in_repo rev-parse --is-inside-work-tree >/dev/null 2>&1 || fail "git repository not found at $FLOWSTOCK_REPO_DIR"
}

ensure_git_clean_worktree() {
    if ! git_in_repo diff --quiet --ignore-submodules -- || ! git_in_repo diff --cached --quiet --ignore-submodules --; then
        git_in_repo status --short >&2 || true
        fail "git worktree has uncommitted tracked changes; commit or revert them before deploy"
    fi
}

is_ipv4_like() {
    local value="$1"
    [[ "$value" =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]]
}

default_tls_san() {
    if is_ipv4_like "$FLOWSTOCK_TLS_SERVER_NAME"; then
        printf 'IP:%s\n' "$FLOWSTOCK_TLS_SERVER_NAME"
    else
        printf 'DNS:%s\n' "$FLOWSTOCK_TLS_SERVER_NAME"
    fi
}

normalized_tls_sans() {
    local primary_san
    local sans

    primary_san="$(default_tls_san)"
    sans="${FLOWSTOCK_TLS_SANS:-$primary_san}"
    if [[ ",$sans," != *",$primary_san,"* ]]; then
        sans="${primary_san},${sans}"
    fi

    printf '%s\n' "$sans"
}

ensure_tls_assets() {
    mkdir -p "$FLOWSTOCK_TLS_CERT_DIR"

    case "$FLOWSTOCK_TLS_MODE" in
        manual|"")
            require_file "$FLOWSTOCK_TLS_CERT_PATH"
            require_file "$FLOWSTOCK_TLS_KEY_PATH"
            ;;
        local_ca)
            require_command openssl
            bash "${SCRIPT_DIR}/renew_server_cert.sh"
            ;;
        *)
            fail "unsupported FLOWSTOCK_TLS_MODE: $FLOWSTOCK_TLS_MODE"
            ;;
    esac
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

release_state_file() {
    local name="$1"
    ensure_runtime_dirs
    printf '%s/%s.env\n' "$FLOWSTOCK_RELEASES_DIR" "$name"
}

release_history_file() {
    local stamp="$1"
    ensure_runtime_dirs
    mkdir -p "$FLOWSTOCK_RELEASES_DIR/history"
    printf '%s/history/%s.env\n' "$FLOWSTOCK_RELEASES_DIR" "$stamp"
}

write_release_state() {
    local path="$1"
    shift

    mkdir -p "$(dirname "$path")"
    : >"$path"
    for pair in "$@"; do
        local key="${pair%%=*}"
        local value="${pair#*=}"
        printf '%s=%q\n' "$key" "$value" >>"$path"
    done
}
