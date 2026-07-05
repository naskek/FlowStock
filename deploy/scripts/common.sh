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
FLOWSTOCK_DISCOVERY_BACKEND_PORT="${FLOWSTOCK_DISCOVERY_BACKEND_PORT:-17155}"
FLOWSTOCK_DISCOVERY_RELAY_TIMEOUT_MS="${FLOWSTOCK_DISCOVERY_RELAY_TIMEOUT_MS:-2000}"
FLOWSTOCK_DISCOVERY_RELAY_MAX_IN_FLIGHT="${FLOWSTOCK_DISCOVERY_RELAY_MAX_IN_FLIGHT:-64}"

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

public_base_url_host() {
    local value="${FLOWSTOCK_PUBLIC_BASE_URL:-}"
    local authority

    [[ -n "$value" ]] || fail "FLOWSTOCK_PUBLIC_BASE_URL must not be empty"
    [[ "$value" == https://* ]] || fail "FLOWSTOCK_PUBLIC_BASE_URL must start with https://"
    authority="${value#https://}"
    [[ "$authority" != *"/"* ]] || fail "FLOWSTOCK_PUBLIC_BASE_URL must not contain a path"
    [[ "$authority" != *"?"* && "$authority" != *"#"* ]] || fail "FLOWSTOCK_PUBLIC_BASE_URL must not contain query or fragment"
    [[ "$authority" != *"@"* ]] || fail "FLOWSTOCK_PUBLIC_BASE_URL must not contain userinfo"
    [[ -n "$authority" ]] || fail "FLOWSTOCK_PUBLIC_BASE_URL host must not be empty"

    if [[ "$authority" == *":"* ]]; then
        local port="${authority##*:}"
        [[ "$port" =~ ^[0-9]+$ ]] || fail "FLOWSTOCK_PUBLIC_BASE_URL port must be numeric"
        (( port >= 1 && port <= 65535 )) || fail "FLOWSTOCK_PUBLIC_BASE_URL port must be in range 1..65535"
        printf '%s\n' "${authority%:*}"
    else
        printf '%s\n' "$authority"
    fi
}

validate_public_base_url_matches_tls_config() {
    local host
    local expected_sans
    local expected_token

    host="$(public_base_url_host)"
    expected_sans="$(normalized_tls_sans)"
    if [[ "$FLOWSTOCK_TLS_SERVER_NAME" == "$host" ]]; then
        return 0
    fi

    if is_ipv4_like "$host"; then
        expected_token="IP:${host}"
    else
        expected_token="DNS:${host}"
    fi

    if [[ ",${expected_sans}," != *",${expected_token},"* ]]; then
        fail "FLOWSTOCK_PUBLIC_BASE_URL host must match FLOWSTOCK_TLS_SERVER_NAME or FLOWSTOCK_TLS_SANS"
    fi
}

validate_public_base_url_matches_certificate() {
    local host
    local expected_token
    local actual_sans

    require_command openssl
    require_file "$FLOWSTOCK_TLS_CERT_PATH"
    host="$(public_base_url_host)"
    if is_ipv4_like "$host"; then
        expected_token="IP:${host}"
    else
        expected_token="DNS:${host}"
    fi

    actual_sans="$(openssl x509 -in "$FLOWSTOCK_TLS_CERT_PATH" -noout -ext subjectAltName 2>/dev/null \
        | tail -n +2 \
        | tr -d ' \n' \
        | sed 's/IPAddress:/IP:/g')"
    if [[ ",${actual_sans}," != *",${expected_token},"* ]]; then
        fail "server certificate SAN does not contain FLOWSTOCK_PUBLIC_BASE_URL host"
    fi
}

ensure_tls_assets() {
    mkdir -p "$FLOWSTOCK_TLS_CERT_DIR"
    validate_public_base_url_matches_tls_config

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
    validate_public_base_url_matches_certificate
}

compose() {
    docker compose \
        --project-name "$FLOWSTOCK_PROJECT_NAME" \
        --env-file "$FLOWSTOCK_ENV_FILE" \
        -f "$FLOWSTOCK_COMPOSE_FILE" \
        "$@"
}

validate_int_range() {
    local name="$1"
    local value="$2"
    local min="$3"
    local max="$4"
    [[ "$value" =~ ^[0-9]+$ ]] || fail "$name must be an integer"
    (( value >= min && value <= max )) || fail "$name must be in range ${min}..${max}"
}

validate_discovery_env() {
    validate_int_range FLOWSTOCK_DISCOVERY_BACKEND_PORT "$FLOWSTOCK_DISCOVERY_BACKEND_PORT" 1 65535
    (( FLOWSTOCK_DISCOVERY_BACKEND_PORT != 7155 )) || fail "FLOWSTOCK_DISCOVERY_BACKEND_PORT must not be 7155"
    validate_int_range FLOWSTOCK_DISCOVERY_RELAY_TIMEOUT_MS "$FLOWSTOCK_DISCOVERY_RELAY_TIMEOUT_MS" 100 10000
    validate_int_range FLOWSTOCK_DISCOVERY_RELAY_MAX_IN_FLIGHT "$FLOWSTOCK_DISCOVERY_RELAY_MAX_IN_FLIGHT" 1 512
    if [[ -n "${FLOWSTOCK_DISCOVERY_BEHIND_RELAY:-}" ]]; then
        [[ "$FLOWSTOCK_DISCOVERY_BEHIND_RELAY" == "0" || "$FLOWSTOCK_DISCOVERY_BEHIND_RELAY" == "1" ]] \
            || fail "FLOWSTOCK_DISCOVERY_BEHIND_RELAY must be 0 or 1"
    fi
}

compose_services() {
    compose config --services 2>/dev/null || true
}

service_exists() {
    local service="$1"
    compose_services | grep -qx "$service"
}

existing_services() {
    for service in "$@"; do
        if service_exists "$service"; then
            printf '%s\n' "$service"
        fi
    done
}

compose_logs_existing() {
    local services=()
    while IFS= read -r service; do
        services+=("$service")
    done < <(existing_services "$@")
    if (( ${#services[@]} > 0 )); then
        compose logs --tail=80 "${services[@]}" || true
    fi
}

compose_stop_existing() {
    local services=()
    while IFS= read -r service; do
        services+=("$service")
    done < <(existing_services "$@")
    if (( ${#services[@]} > 0 )); then
        compose stop "${services[@]}" || true
    fi
}

remove_discovery_relay_containers() {
    local container_ids
    container_ids="$(docker ps -aq \
        --filter "label=com.docker.compose.project=${FLOWSTOCK_PROJECT_NAME}" \
        --filter "label=com.docker.compose.service=discovery-relay" || true)"
    if [[ -z "$container_ids" ]]; then
        return 0
    fi

    log "removing discovery-relay containers before backend change"
    while IFS= read -r container_id; do
        [[ -n "$container_id" ]] || continue
        local state
        state="$(docker inspect -f '{{.State.Status}}' "$container_id")"
        if [[ "$state" == "running" ]]; then
            docker stop -t 5 "$container_id" >/dev/null
        fi

        local exit_code
        exit_code="$(docker inspect -f '{{.State.ExitCode}}' "$container_id")"
        [[ "$exit_code" != "137" ]] || fail "discovery-relay was killed during graceful stop"
        docker rm "$container_id" >/dev/null
    done <<<"$container_ids"
}

assert_no_discovery_relay_containers() {
    local container_ids
    container_ids="$(docker ps -aq \
        --filter "label=com.docker.compose.project=${FLOWSTOCK_PROJECT_NAME}" \
        --filter "label=com.docker.compose.service=discovery-relay" || true)"
    [[ -z "$container_ids" ]] || fail "discovery-relay container still exists after removal"
}

ss_udp_lines() {
    if ! command -v ss >/dev/null 2>&1; then
        return 1
    fi
    ss -lun 2>/dev/null | grep -E ":(7155|${FLOWSTOCK_DISCOVERY_BACKEND_PORT})([[:space:]]|$)" || true
}

require_udp_port_free() {
    local port="$1"
    if ! command -v ss >/dev/null 2>&1; then
        log "WARNING: ss is not available; cannot verify UDP port $port"
        return 0
    fi
    if ss -lun 2>/dev/null | grep -E ":${port}([[:space:]]|$)" >/dev/null; then
        ss -lun | grep -E ":${port}([[:space:]]|$)" >&2 || true
        fail "UDP port $port is still bound"
    fi
}

allow_udp_7155_free_or_current_flowstock_publish() {
    if ! command -v ss >/dev/null 2>&1; then
        fail "ss is required to verify UDP 7155 owner before recreating flowstock"
    fi

    if ! ss -lun 2>/dev/null | grep -E ":7155([[:space:]]|$)" >/dev/null; then
        log "UDP 7155 is free before flowstock recreate"
        return 0
    fi

    local flowstock_id
    flowstock_id="$(service_container_id flowstock || true)"
    if [[ -z "$flowstock_id" ]]; then
        ss -lunp 2>/dev/null | grep -E ":7155([[:space:]]|$)" >&2 || ss -lun | grep -E ":7155([[:space:]]|$)" >&2 || true
        fail "UDP 7155 is bound, but current compose project has no flowstock container"
    fi

    local flowstock_status
    flowstock_status="$(docker inspect -f '{{.State.Status}}' "$flowstock_id" 2>/dev/null || true)"
    if [[ "$flowstock_status" != "running" ]]; then
        fail "UDP 7155 is bound, but current flowstock container is not running"
    fi

    local published
    published="$(docker port "$flowstock_id" 7155/udp 2>/dev/null || true)"
    if grep -Eq ':7155$' <<<"$published"; then
        log "UDP 7155 is held by current flowstock legacy publish; allowing recreate to release it"
        return 0
    fi

    ss -lunp 2>/dev/null | grep -E ":7155([[:space:]]|$)" >&2 || ss -lun | grep -E ":7155([[:space:]]|$)" >&2 || true
    fail "UDP 7155 is bound by an unexpected owner"
}

require_udp_port_bound() {
    local port="$1"
    if ! command -v ss >/dev/null 2>&1; then
        log "WARNING: ss is not available; cannot verify UDP port $port"
        return 0
    fi
    if ! ss -lun 2>/dev/null | grep -E ":${port}([[:space:]]|$)" >/dev/null; then
        fail "UDP port $port is not bound"
    fi
}

discovery_network_preflight() {
    log "checking UDP discovery ports with ss"
    if ! command -v ss >/dev/null 2>&1; then
        log "WARNING: ss is not available; skipping UDP port preflight"
    else
        ss_udp_lines || true
    fi

    if command -v nft >/dev/null 2>&1; then
        if sudo -n nft list ruleset >/dev/null 2>&1; then
            log "firewall nft ruleset is readable"
        elif nft list ruleset >/dev/null 2>&1; then
            log "firewall nft ruleset is readable"
        else
            log "WARNING: firewall nft state is not readable; inbound UDP 7155 must be allowed from operator LAN"
        fi
    elif command -v ufw >/dev/null 2>&1; then
        if sudo -n ufw status >/dev/null 2>&1; then
            log "firewall ufw status is readable"
        elif ufw status >/dev/null 2>&1; then
            log "firewall ufw status is readable"
        else
            log "WARNING: firewall ufw state is not readable; inbound UDP 7155 must be allowed from operator LAN"
        fi
    else
        log "WARNING: nft/ufw not found; firewall state was not inspected"
    fi
}

check_discovery_backend() {
    service_exists discovery-relay || return 0
    log "checking direct UDP discovery backend on 127.0.0.1:${FLOWSTOCK_DISCOVERY_BACKEND_PORT}"
    compose run --rm --no-deps --entrypoint dotnet discovery-relay FlowStock.DiscoveryRelay.dll backend-healthcheck
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
    validate_discovery_env
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
