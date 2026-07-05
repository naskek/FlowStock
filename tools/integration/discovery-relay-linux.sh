#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="${FLOWSTOCK_REPO_DIR:-$(cd -- "${SCRIPT_DIR}/../.." && pwd)}"
RUN_ID="fsrelay-$RANDOM-$(date +%s)"
WORK_ROOT=""
RELAY_IMAGE=""
RELAY_CONTAINER=""
BACKEND_PID=""
ASYNC_CLIENT_PID=""
NETNS=""
VETH_HOST=""
VETH_NS=""

PROJECT_NAMES=()
PROJECT_COMPOSE_FILES=()
PROJECT_ENV_FILES=()
WORKTREES=()
ADDED_WORKTREE=""

PUBLIC_PORT=7155
BACKEND_PORT="${FLOWSTOCK_DISCOVERY_BACKEND_PORT:-17155}"
OLD_REF="${FLOWSTOCK_DISCOVERY_OLD_REF:-16daae2}"
REF_A="${FLOWSTOCK_DISCOVERY_RELAY_REF_A:-}"
REF_B="${FLOWSTOCK_DISCOVERY_RELAY_REF_B:-HEAD}"

log() {
    printf '[flowstock-discovery-it] %s\n' "$*"
}

skip() {
    printf '[flowstock-discovery-it] SKIP: %s\n' "$*" >&2
    exit 77
}

fail() {
    printf '[flowstock-discovery-it] ERROR: %s\n' "$*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || skip "required command is not available: $1"
}

compose_cmd() {
    local project="$1"
    local compose_file="$2"
    local env_file="$3"
    shift 3
    docker compose --project-name "$project" --env-file "$env_file" -f "$compose_file" "$@"
}

register_project() {
    PROJECT_NAMES+=("$1")
    PROJECT_COMPOSE_FILES+=("$2")
    PROJECT_ENV_FILES+=("$3")
}

assert_project_has_no_containers() {
    local project="$1"
    local ids
    ids="$(docker ps -aq --filter "label=com.docker.compose.project=${project}" || true)"
    [[ -z "$ids" ]] || fail "compose project ${project} still has containers after cleanup"
}

assert_project_has_no_volumes_or_networks() {
    local project="$1"
    local volumes networks
    volumes="$(docker volume ls -q --filter "label=com.docker.compose.project=${project}" || true)"
    networks="$(docker network ls -q --filter "label=com.docker.compose.project=${project}" || true)"
    [[ -z "$volumes" ]] || fail "compose project ${project} still has volumes after cleanup: ${volumes}"
    [[ -z "$networks" ]] || fail "compose project ${project} still has networks after cleanup: ${networks}"
}

cleanup_project() {
    local project="$1"
    local compose_file="$2"
    local env_file="$3"
    log "cleaning isolated compose project ${project}"
    compose_cmd "$project" "$compose_file" "$env_file" down -v --remove-orphans >/dev/null 2>&1 || true
    assert_project_has_no_containers "$project"
    assert_project_has_no_volumes_or_networks "$project"
    require_udp_port_free "$PUBLIC_PORT"
    require_udp_port_free "$BACKEND_PORT"
}

cleanup_registered_projects() {
    (( ${#PROJECT_NAMES[@]} > 0 )) || return 0
    local index failed=0 ids volumes networks
    for index in "${!PROJECT_NAMES[@]}"; do
        compose_cmd "${PROJECT_NAMES[$index]}" "${PROJECT_COMPOSE_FILES[$index]}" "${PROJECT_ENV_FILES[$index]}" down -v --remove-orphans >/dev/null 2>&1 || true
    done
    for index in "${!PROJECT_NAMES[@]}"; do
        ids="$(docker ps -aq --filter "label=com.docker.compose.project=${PROJECT_NAMES[$index]}" || true)"
        if [[ -n "$ids" ]]; then
            log "ERROR: compose project ${PROJECT_NAMES[$index]} still has containers after cleanup"
            failed=1
        fi
        volumes="$(docker volume ls -q --filter "label=com.docker.compose.project=${PROJECT_NAMES[$index]}" || true)"
        if [[ -n "$volumes" ]]; then
            log "ERROR: compose project ${PROJECT_NAMES[$index]} still has volumes after cleanup: ${volumes}"
            failed=1
        fi
        networks="$(docker network ls -q --filter "label=com.docker.compose.project=${PROJECT_NAMES[$index]}" || true)"
        if [[ -n "$networks" ]]; then
            log "ERROR: compose project ${PROJECT_NAMES[$index]} still has networks after cleanup: ${networks}"
            failed=1
        fi
    done
    if command -v ss >/dev/null 2>&1; then
        if ss -lun | grep -E ":${PUBLIC_PORT}([[:space:]]|$)" >/dev/null; then
            ss -lunp | grep -E ":${PUBLIC_PORT}([[:space:]]|$)" >&2 || true
            log "ERROR: UDP port ${PUBLIC_PORT} is still bound after cleanup"
            failed=1
        fi
        if ss -lun | grep -E ":${BACKEND_PORT}([[:space:]]|$)" >/dev/null; then
            ss -lunp | grep -E ":${BACKEND_PORT}([[:space:]]|$)" >&2 || true
            log "ERROR: UDP port ${BACKEND_PORT} is still bound after cleanup"
            failed=1
        fi
    fi
    return "$failed"
}

cleanup_worktrees() {
    (( ${#WORKTREES[@]} > 0 )) || return 0
    local worktree failed=0 listing
    for worktree in "${WORKTREES[@]}"; do
        git -C "$REPO_DIR" worktree remove --force "$worktree" >/dev/null 2>&1 || true
    done
    listing="$(git -C "$REPO_DIR" worktree list --porcelain 2>/dev/null || true)"
    for worktree in "${WORKTREES[@]}"; do
        if [[ -e "$worktree" ]]; then
            log "ERROR: temporary worktree still exists after cleanup: ${worktree}"
            failed=1
        fi
        if grep -Fxq "worktree ${worktree}" <<<"$listing"; then
            log "ERROR: git worktree metadata still contains: ${worktree}"
            failed=1
        fi
    done
    return "$failed"
}

cleanup() {
    local exit_code=$?
    local cleanup_failed=0
    set +e
    if [[ -n "$RELAY_CONTAINER" ]]; then
        docker rm -f "$RELAY_CONTAINER" >/dev/null 2>&1
    fi
    if [[ -n "${ASYNC_CLIENT_PID:-}" ]]; then
        kill "$ASYNC_CLIENT_PID" >/dev/null 2>&1
        wait "$ASYNC_CLIENT_PID" >/dev/null 2>&1
    fi
    stop_backend
    cleanup_namespace
    cleanup_registered_projects || cleanup_failed=1
    if cleanup_worktrees; then
        if [[ -n "$WORK_ROOT" ]]; then
            rm -rf "$WORK_ROOT"
        fi
    else
        cleanup_failed=1
    fi
    if (( cleanup_failed != 0 && exit_code == 0 )); then
        exit 1
    fi
    if [[ -n "$WORK_ROOT" && ! -e "$WORK_ROOT" ]]; then
        :
    elif [[ -n "$WORK_ROOT" && cleanup_failed == 0 ]]; then
        rm -rf "$WORK_ROOT"
    fi
    exit "$exit_code"
}
trap cleanup EXIT

ensure_linux_prereqs() {
    [[ "$(uname -s)" == "Linux" ]] || skip "Linux-only test; run in WSL2, disposable Debian VM, or Linux CI runner"
    [[ "${EUID:-$(id -u)}" -eq 0 ]] || skip "root/CAP_NET_ADMIN is required for network namespace and veth setup"
    require_command docker
    require_command git
    require_command ip
    require_command ss
    require_command python3
    require_command curl
    require_command openssl
    docker info >/dev/null 2>&1 || skip "Docker daemon is not reachable"
    WORK_ROOT="$(mktemp -d "/tmp/${RUN_ID}.XXXXXX")"
}

require_udp_port_free() {
    local port="$1"
    if ss -lun | grep -E ":${port}([[:space:]]|$)" >/dev/null; then
        ss -lunp | grep -E ":${port}([[:space:]]|$)" >&2 || true
        fail "UDP port ${port} is still bound"
    fi
}

wait_for_service_status() {
    local project="$1"
    local service="$2"
    local expected="$3"
    local timeout="${4:-120}"
    local started_at
    started_at="$(date +%s)"
    while true; do
        local container_id status
        container_id="$(docker ps -aq \
            --filter "label=com.docker.compose.project=${project}" \
            --filter "label=com.docker.compose.service=${service}" | head -n 1)"
        if [[ -n "$container_id" ]]; then
            status="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$container_id")"
            [[ "$status" == "$expected" ]] && return 0
        fi
        (( "$(date +%s)" - started_at < timeout )) || fail "timeout waiting for ${project}/${service} to become ${expected}"
        sleep 2
    done
}

assert_no_relay_container() {
    local project="$1"
    local relay_ids
    relay_ids="$(docker ps -aq \
        --filter "label=com.docker.compose.project=${project}" \
        --filter "label=com.docker.compose.service=discovery-relay" || true)"
    [[ -z "$relay_ids" ]] || fail "unexpected discovery-relay container remains in project ${project}"
}

assert_no_orphan_relay() {
    local project="$1"
    local expected_count="$2"
    local ids
    mapfile -t ids < <(docker ps -aq \
        --filter "label=com.docker.compose.project=${project}" \
        --filter "label=com.docker.compose.service=discovery-relay")
    [[ "${#ids[@]}" -eq "$expected_count" ]] \
        || fail "expected ${expected_count} discovery-relay containers in ${project}, found ${#ids[@]}"
}

assert_exactly_one_service_container() {
    local project="$1"
    local service="$2"
    local ids
    mapfile -t ids < <(docker ps -aq \
        --filter "label=com.docker.compose.project=${project}" \
        --filter "label=com.docker.compose.service=${service}")
    [[ "${#ids[@]}" -eq 1 ]] || fail "expected exactly one ${project}/${service} container, found ${#ids[@]}"
    printf '%s\n' "${ids[0]}"
}

assert_service_strict_status() {
    local project="$1"
    local service="$2"
    local expected="$3"
    local container_id status
    container_id="$(assert_exactly_one_service_container "$project" "$service")"
    status="$(docker inspect -f '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$container_id")"
    [[ "$status" == "$expected" ]] || fail "${project}/${service} status is ${status}, expected ${expected}"
}

assert_relay_revision_ready() {
    local project="$1"
    local compose_file="$2"
    local env_file="$3"
    local relay_id
    assert_no_orphan_relay "$project" 1
    assert_service_strict_status "$project" flowstock healthy
    assert_service_strict_status "$project" discovery-relay healthy
    relay_id="$(assert_exactly_one_service_container "$project" discovery-relay)"
    assert_host_port_owned_by_container_pid "$relay_id" "$PUBLIC_PORT"
    compose_cmd "$project" "$compose_file" "$env_file" run --rm --no-deps --entrypoint dotnet discovery-relay FlowStock.DiscoveryRelay.dll healthcheck
}

assert_host_port_owned_by_container_pid() {
    local container_id="$1"
    local port="$2"
    local ss_pids
    ss_pids="$(ss -lunp | awk -v port=":${port}" '$0 ~ port { while (match($0, /pid=[0-9]+/)) { print substr($0, RSTART + 4, RLENGTH - 4); $0 = substr($0, RSTART + RLENGTH) } }' | sort -u)"
    [[ -n "$ss_pids" ]] || fail "host UDP ${port} is not bound by a visible process"
    local container_pids
    container_pids="$(docker top "$container_id" -eo pid | tail -n +2 | tr -d ' ' | sort -u)"
    while IFS= read -r pid; do
        [[ -n "$pid" ]] || continue
        if grep -qx "$pid" <<<"$container_pids"; then
            return 0
        fi
    done <<<"$ss_pids"
    fail "host UDP ${port} is not owned by expected container ${container_id}"
}

assert_old_revision_ready() {
    local project="$1"
    assert_no_orphan_relay "$project" 0
    assert_no_relay_container "$project"
    local flowstock_id
    flowstock_id="$(assert_exactly_one_service_container "$project" flowstock)"
    docker port "$flowstock_id" 7155/udp | grep -q "0.0.0.0:${PUBLIC_PORT}\\|127.0.0.1:${PUBLIC_PORT}\\|:::${PUBLIC_PORT}" \
        || fail "old flowstock container does not publish UDP ${PUBLIC_PORT}"
    protocol_request 127.0.0.1 "$PUBLIC_PORT"
}

protocol_request() {
    local host="$1"
    local port="$2"
    python3 - "$host" "$port" <<'PY'
import json
import socket
import sys

host = sys.argv[1]
port = int(sys.argv[2])
nonce = "0123456789abcdef0123456789abcdef"
payload = json.dumps({
    "product": "FlowStock",
    "discovery_protocol_version": 1,
    "nonce": nonce,
}).encode()
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.settimeout(5)
sock.sendto(payload, (host, port))
data, addr = sock.recvfrom(1025)
response = json.loads(data.decode())
assert response["product"] == "FlowStock"
assert response["discovery_protocol_version"] == 1
assert response["nonce"] == nonce
print(f"ok protocol {addr[0]}:{addr[1]}")
PY
}

build_relay_image() {
    RELAY_IMAGE="${RUN_ID}:discovery-relay"
    log "building discovery-relay image ${RELAY_IMAGE}"
    docker build \
        -f "${REPO_DIR}/deploy/Dockerfile" \
        --target discovery-relay-runtime \
        -t "$RELAY_IMAGE" \
        "$REPO_DIR"
}

start_backend() {
    local mode="$1"
    local response_size="${2:-0}"
    local signal_file="${3:-}"
    stop_backend
    python3 -u - "$BACKEND_PORT" "$mode" "$response_size" "$signal_file" <<'PY' &
import pathlib
import socket
import sys

port = int(sys.argv[1])
mode = sys.argv[2]
response_size = int(sys.argv[3])
signal_file = sys.argv[4]
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.bind(("127.0.0.1", port))
while True:
    data, addr = sock.recvfrom(65535)
    if signal_file:
        pathlib.Path(signal_file).write_text("received\n")
    if mode == "timeout":
        continue
    if mode == "oversized":
        sock.sendto(b"r" * response_size, addr)
    else:
        sock.sendto(data, addr)
PY
    BACKEND_PID="$!"
    sleep 0.2
}

stop_backend() {
    if [[ -n "${BACKEND_PID:-}" ]]; then
        kill "$BACKEND_PID" >/dev/null 2>&1 || true
        wait "$BACKEND_PID" >/dev/null 2>&1 || true
        BACKEND_PID=""
    fi
}

cleanup_namespace() {
    if [[ -n "${NETNS:-}" ]]; then
        ip netns delete "$NETNS" >/dev/null 2>&1 || true
        NETNS=""
    fi
    if [[ -n "${VETH_HOST:-}" ]]; then
        ip link delete "$VETH_HOST" >/dev/null 2>&1 || true
        VETH_HOST=""
        VETH_NS=""
    fi
}

start_relay_container() {
    RELAY_CONTAINER="${RUN_ID}-relay"
    docker rm -f "$RELAY_CONTAINER" >/dev/null 2>&1 || true
    docker run -d \
        --name "$RELAY_CONTAINER" \
        --network host \
        --read-only \
        --cap-drop ALL \
        --security-opt no-new-privileges:true \
        -e "FLOWSTOCK_DISCOVERY_BACKEND_PORT=${BACKEND_PORT}" \
        -e "FLOWSTOCK_DISCOVERY_RELAY_TIMEOUT_MS=10000" \
        -e "FLOWSTOCK_DISCOVERY_RELAY_MAX_IN_FLIGHT=16" \
        "$RELAY_IMAGE" >/dev/null
    for _ in $(seq 1 50); do
        if ss -lun | grep -E ":${PUBLIC_PORT}([[:space:]]|$)" >/dev/null; then
            return 0
        fi
        sleep 0.1
    done
    docker logs "$RELAY_CONTAINER" >&2 || true
    fail "relay did not bind UDP ${PUBLIC_PORT}"
}

create_namespace() {
    NETNS="${RUN_ID}-ns"
    VETH_HOST="${RUN_ID:0:12}h"
    VETH_NS="${RUN_ID:0:12}n"
    ip netns add "$NETNS"
    ip link add "$VETH_HOST" type veth peer name "$VETH_NS"
    ip link set "$VETH_NS" netns "$NETNS"
    ip addr add 10.77.55.1/24 dev "$VETH_HOST"
    ip link set "$VETH_HOST" up
    ip netns exec "$NETNS" ip addr add 10.77.55.2/24 dev "$VETH_NS"
    ip netns exec "$NETNS" ip link set lo up
    ip netns exec "$NETNS" ip link set "$VETH_NS" up
}

namespace_client() {
    local payload_size="$1"
    local source_port="$2"
    local expect_response="$3"
    local timeout="${4:-2}"
    ip netns exec "$NETNS" python3 - "$PUBLIC_PORT" "$source_port" "$expect_response" "$payload_size" "$timeout" <<'PY'
import socket
import sys

public_port = int(sys.argv[1])
source_port = int(sys.argv[2])
expect_response = sys.argv[3] == "yes"
payload = (b"x" * int(sys.argv[4]))
timeout = float(sys.argv[5])
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
sock.bind(("10.77.55.2", source_port))
sock.settimeout(timeout)
sock.sendto(payload, ("10.77.55.255", public_port))
try:
    data, addr = sock.recvfrom(65535)
except socket.timeout:
    if expect_response:
        raise SystemExit("expected response timed out")
    raise SystemExit(0)
if not expect_response:
    raise SystemExit("unexpected response")
if data != payload:
    raise SystemExit("response payload mismatch")
if addr[0] != "10.77.55.1":
    raise SystemExit(f"response source IP mismatch: {addr[0]}")
if addr[1] != public_port:
    raise SystemExit(f"response source port mismatch: {addr[1]}")
print(f"ok source_ip=10.77.55.2 source_port={source_port} reply={addr[0]}:{addr[1]}")
PY
}

namespace_client_async_waiting_for_response() {
    local source_port="$1"
    local marker="$2"
    local log_file="$3"
    ip netns exec "$NETNS" python3 - "$PUBLIC_PORT" "$source_port" "$marker" >"$log_file" 2>&1 <<'PY' &
import pathlib
import socket
import sys

public_port = int(sys.argv[1])
source_port = int(sys.argv[2])
marker = pathlib.Path(sys.argv[3])
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
sock.bind(("10.77.55.2", source_port))
sock.settimeout(15)
sock.sendto(b"pending-stop", ("10.77.55.255", public_port))
marker.write_text("sent\n")
try:
    sock.recvfrom(65535)
except socket.timeout:
    pass
PY
    ASYNC_CLIENT_PID="$!"
}

namespace_concurrent_clients() {
    ip netns exec "$NETNS" python3 - "$PUBLIC_PORT" <<'PY'
import concurrent.futures
import socket
import sys

public_port = int(sys.argv[1])

def one(i):
    payload = f"client-{i}".encode()
    port = 38000 + i
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
    sock.bind(("10.77.55.2", port))
    sock.settimeout(2)
    sock.sendto(payload, ("10.77.55.255", public_port))
    data, addr = sock.recvfrom(65535)
    if data != payload or addr != ("10.77.55.1", public_port):
        raise RuntimeError(f"client {i} got {data!r} from {addr}")
    return port

with concurrent.futures.ThreadPoolExecutor(max_workers=8) as executor:
    ports = list(executor.map(one, range(8)))
print("ok concurrent", ",".join(map(str, ports)))
PY
}

run_network_scenarios() {
    require_udp_port_free "$PUBLIC_PORT"
    require_udp_port_free "$BACKEND_PORT"
    build_relay_image
    start_backend echo
    start_relay_container
    create_namespace

    log "checking directed broadcast and same source IP/source port response"
    namespace_client 64 37155 yes

    log "checking exact 1024-byte datagram"
    namespace_client 1024 37156 yes

    log "checking oversized request drops"
    namespace_client 1025 37157 no
    namespace_client 4096 37158 no

    log "checking concurrent clients without cross-talk"
    namespace_concurrent_clients

    log "checking oversized backend response drops"
    start_backend oversized 1025
    namespace_client 32 37159 no
    start_backend oversized 4096
    namespace_client 32 37160 no

    log "checking backend timeout"
    start_backend timeout
    namespace_client 32 37161 no 1

    log "checking graceful docker stop with pending worker"
    local backend_marker client_marker client_log started_ms stop_elapsed_ms
    backend_marker="${WORK_ROOT}/backend-received"
    client_marker="${WORK_ROOT}/client-sent"
    client_log="${WORK_ROOT}/graceful-stop-client.log"
    rm -f "$backend_marker" "$client_marker" "$client_log"
    start_backend timeout 0 "$backend_marker"
    started_ms="$(date +%s%3N)"
    namespace_client_async_waiting_for_response 37162 "$client_marker" "$client_log"
    for _ in $(seq 1 50); do
        [[ -f "$backend_marker" ]] && break
        sleep 0.1
    done
    [[ -f "$backend_marker" ]] || fail "backend receive did not start before docker stop"
    [[ -f "$client_marker" ]] || fail "async client did not send request"
    stop_elapsed_ms="$(( $(date +%s%3N) - started_ms ))"
    (( stop_elapsed_ms < 5000 )) || fail "docker stop was delayed ${stop_elapsed_ms}ms; expected substantially less than backend timeout"
    docker stop -t 5 "$RELAY_CONTAINER" >/dev/null
    local exit_code
    exit_code="$(docker inspect -f '{{.State.ExitCode}}' "$RELAY_CONTAINER")"
    [[ "$exit_code" == "0" ]] || fail "relay docker stop exit code was ${exit_code}, expected 0"
    [[ "$exit_code" != "137" ]] || fail "relay required SIGKILL during docker stop"
    docker rm "$RELAY_CONTAINER" >/dev/null
    RELAY_CONTAINER=""
    require_udp_port_free "$PUBLIC_PORT"
    if [[ -n "$ASYNC_CLIENT_PID" ]]; then
        for _ in $(seq 1 20); do
            if ! kill -0 "$ASYNC_CLIENT_PID" >/dev/null 2>&1; then
                break
            fi
            sleep 0.1
        done
        if kill -0 "$ASYNC_CLIENT_PID" >/dev/null 2>&1; then
            kill "$ASYNC_CLIENT_PID" >/dev/null 2>&1 || true
        fi
        wait "$ASYNC_CLIENT_PID" || true
        ASYNC_CLIENT_PID=""
    fi
    stop_backend
    cleanup_namespace
}

copy_env_for_worktree() {
    local worktree="$1"
    local env_file="$2"
    local app_port="$((20000 + RANDOM % 10000))"
    local https_port="$((30000 + RANDOM % 10000))"
    cat >"$env_file" <<EOF
POSTGRES_DB=flowstock
POSTGRES_USER=flowstock
POSTGRES_PASSWORD=change_me_strong
FLOWSTOCK_PG_BIND_HOST=127.0.0.1
FLOWSTOCK_PORT=${app_port}
FLOWSTOCK_HTTPS_PORT=${https_port}
FLOWSTOCK_DISCOVERY_BACKEND_PORT=${BACKEND_PORT}
FLOWSTOCK_DISCOVERY_RELAY_TIMEOUT_MS=500
FLOWSTOCK_DISCOVERY_RELAY_MAX_IN_FLIGHT=16
FLOWSTOCK_PUBLIC_BASE_URL=https://flowstock.local:${https_port}
FLOWSTOCK_INSTANCE_NAME=FlowStock
FLOWSTOCK_TLS_MODE=local_ca
FLOWSTOCK_TLS_SERVER_NAME=flowstock.local
FLOWSTOCK_TLS_SANS=DNS:flowstock.local
FLOWSTOCK_CA_DIR=${WORK_ROOT}/ca
BACKUP_KEEP=1
BACKUP_INTERVAL_SECONDS=86400
POSTGRES_WAIT_TIMEOUT_SECONDS=120
EOF
}

env_value() {
    local env_file="$1"
    local key="$2"
    awk -F= -v key="$key" '$1 == key { print substr($0, index($0, "=") + 1) }' "$env_file" | tail -n 1
}

ensure_integration_ca() {
    local ca_dir="${WORK_ROOT}/ca"
    local ca_cert="${ca_dir}/flowstock-root-ca.crt"
    local ca_key="${ca_dir}/flowstock-root-ca.key"
    mkdir -p "$ca_dir"
    if [[ -f "$ca_cert" && -f "$ca_key" ]]; then
        return 0
    fi
    openssl req -x509 -newkey rsa:2048 -nodes \
        -subj "/CN=FlowStock Integration Root CA" \
        -days 7 \
        -keyout "$ca_key" \
        -out "$ca_cert" >/dev/null 2>&1
}

issue_integration_tls_assets() {
    local worktree="$1"
    local cert_dir="${worktree}/deploy/nginx/certs"
    local ca_dir="${WORK_ROOT}/ca"
    local ca_cert="${ca_dir}/flowstock-root-ca.crt"
    local ca_key="${ca_dir}/flowstock-root-ca.key"
    local tmp_dir conf csr
    ensure_integration_ca
    mkdir -p "$cert_dir"
    tmp_dir="$(mktemp -d "${WORK_ROOT}/tls-issue.XXXXXX")"
    conf="${tmp_dir}/flowstock-openssl.cnf"
    csr="${tmp_dir}/flowstock.csr"
    cat >"$conf" <<'EOF'
[req]
distinguished_name = req_distinguished_name
req_extensions = req_ext
prompt = no

[req_distinguished_name]
CN = flowstock.local

[req_ext]
subjectAltName = DNS:flowstock.local
EOF
    openssl req -newkey rsa:2048 -nodes \
        -keyout "${cert_dir}/flowstock.key" \
        -out "$csr" \
        -config "$conf" >/dev/null 2>&1
    openssl x509 -req \
        -in "$csr" \
        -CA "$ca_cert" \
        -CAkey "$ca_key" \
        -CAcreateserial \
        -out "${cert_dir}/flowstock.crt" \
        -days 7 \
        -sha256 \
        -extensions req_ext \
        -extfile "$conf" >/dev/null 2>&1
    rm -rf "$tmp_dir"
}

assert_nginx_running_and_https() {
    local project="$1"
    local env_file="$2"
    local nginx_id status https_port ca_cert
    nginx_id="$(assert_exactly_one_service_container "$project" nginx)"
    status="$(docker inspect -f '{{.State.Status}}' "$nginx_id")"
    [[ "$status" == "running" ]] || fail "${project}/nginx status is ${status}, expected running"
    https_port="$(env_value "$env_file" FLOWSTOCK_HTTPS_PORT)"
    [[ -n "$https_port" ]] || fail "FLOWSTOCK_HTTPS_PORT is missing in ${env_file}"
    ca_cert="${WORK_ROOT}/ca/flowstock-root-ca.crt"
    [[ -f "$ca_cert" ]] || fail "integration CA certificate is missing: ${ca_cert}"
    curl --resolve "flowstock.local:${https_port}:127.0.0.1" \
        --cacert "$ca_cert" \
        -fsS "https://flowstock.local:${https_port}/health/live" >/dev/null
}

add_worktree() {
    local ref="$1"
    local name="$2"
    local path="${WORK_ROOT}/${name}"
    git -C "$REPO_DIR" worktree add --detach "$path" "$ref" >/dev/null
    WORKTREES+=("$path")
    ADDED_WORKTREE="$path"
}

seed_release_metadata() {
    local runtime_dir="$1"
    local current_commit="$2"
    local previous_commit="$3"
    mkdir -p "${runtime_dir}/releases"
    cat >"${runtime_dir}/releases/latest_success.env" <<EOF
deployed_commit=${current_commit}
deployed_ref=${current_commit}
backup_path=
previous_commit=${previous_commit}
previous_branch=detached
deploy_kind=integration
deployed_at=$(date -u +%Y%m%dT%H%M%SZ)
EOF
    cat >"${runtime_dir}/releases/previous_success.env" <<EOF
deployed_commit=${previous_commit}
deployed_ref=${previous_commit}
backup_path=
previous_commit=
previous_branch=detached
deploy_kind=integration
deployed_at=$(date -u +%Y%m%dT%H%M%SZ)
EOF
}

run_scripted_deploy() {
    local worktree="$1"
    local project="$2"
    local env_file="$3"
    local runtime_dir="$4"
    FLOWSTOCK_ENV_FILE="$env_file" \
    FLOWSTOCK_PROJECT_NAME="$project" \
    FLOWSTOCK_RUNTIME_DIR="$runtime_dir" \
    FLOWSTOCK_REPO_DIR="$worktree" \
    FLOWSTOCK_COMPOSE_FILE="${worktree}/deploy/docker-compose.yml" \
        bash "${worktree}/deploy/scripts/deploy_update.sh"
}

run_scripted_rollback() {
    local worktree="$1"
    local project="$2"
    local env_file="$3"
    local runtime_dir="$4"
    local target_commit="$5"
    FLOWSTOCK_ENV_FILE="$env_file" \
    FLOWSTOCK_PROJECT_NAME="$project" \
    FLOWSTOCK_RUNTIME_DIR="$runtime_dir" \
    FLOWSTOCK_REPO_DIR="$worktree" \
    FLOWSTOCK_COMPOSE_FILE="${worktree}/deploy/docker-compose.yml" \
        bash "${worktree}/deploy/scripts/rollback_release.sh" --no-restore "$target_commit"
}

run_revision_transition() {
    local from_ref="$1"
    local to_ref="$2"
    local name="$3"
    local from_dir to_dir project env_file runtime_dir from_commit to_commit compose_file
    add_worktree "$from_ref" "${name}-from"
    from_dir="$ADDED_WORKTREE"
    add_worktree "$to_ref" "${name}-to"
    to_dir="$ADDED_WORKTREE"
    project="${RUN_ID}-${name}"
    env_file="${WORK_ROOT}/${name}.env"
    runtime_dir="${WORK_ROOT}/${name}-runtime"
    compose_file="${to_dir}/deploy/docker-compose.yml"
    register_project "$project" "$compose_file" "$env_file"
    copy_env_for_worktree "$to_dir" "$env_file"
    from_commit="$(git -C "$from_dir" rev-parse HEAD)"
    to_commit="$(git -C "$to_dir" rev-parse HEAD)"

    log "starting isolated ${name} source revision ${from_commit}"
    run_scripted_deploy "$from_dir" "$project" "$env_file" "$runtime_dir"
    assert_old_revision_ready "$project"
    assert_nginx_running_and_https "$project" "$env_file"
    seed_release_metadata "$runtime_dir" "$from_commit" "$OLD_REF"

    log "deploying isolated ${name} target revision ${to_commit}"
    run_scripted_deploy "$to_dir" "$project" "$env_file" "$runtime_dir"
    assert_relay_revision_ready "$project" "$compose_file" "$env_file"
    assert_nginx_running_and_https "$project" "$env_file"
    cleanup_project "$project" "$compose_file" "$env_file"
}

run_relay_update_transition() {
    local from_ref="$1"
    local to_ref="$2"
    local name="$3"
    local from_dir to_dir project env_file runtime_dir compose_file
    add_worktree "$from_ref" "${name}-from"
    from_dir="$ADDED_WORKTREE"
    add_worktree "$to_ref" "${name}-to"
    to_dir="$ADDED_WORKTREE"
    project="${RUN_ID}-${name}"
    env_file="${WORK_ROOT}/${name}.env"
    runtime_dir="${WORK_ROOT}/${name}-runtime"
    compose_file="${to_dir}/deploy/docker-compose.yml"
    register_project "$project" "$compose_file" "$env_file"
    copy_env_for_worktree "$to_dir" "$env_file"

    run_scripted_deploy "$from_dir" "$project" "$env_file" "$runtime_dir"
    assert_relay_revision_ready "$project" "${from_dir}/deploy/docker-compose.yml" "$env_file"
    assert_nginx_running_and_https "$project" "$env_file"
    run_scripted_deploy "$to_dir" "$project" "$env_file" "$runtime_dir"
    assert_relay_revision_ready "$project" "$compose_file" "$env_file"
    assert_nginx_running_and_https "$project" "$env_file"
    run_scripted_deploy "$to_dir" "$project" "$env_file" "$runtime_dir"
    assert_relay_revision_ready "$project" "$compose_file" "$env_file"
    assert_nginx_running_and_https "$project" "$env_file"
    cleanup_project "$project" "$compose_file" "$env_file"
}

run_relay_rollback_transition() {
    local from_ref="$1"
    local to_ref="$2"
    local name="$3"
    local from_dir project env_file runtime_dir target_commit compose_file
    add_worktree "$from_ref" "${name}-from"
    from_dir="$ADDED_WORKTREE"
    project="${RUN_ID}-${name}"
    env_file="${WORK_ROOT}/${name}.env"
    runtime_dir="${WORK_ROOT}/${name}-runtime"
    compose_file="${from_dir}/deploy/docker-compose.yml"
    register_project "$project" "$compose_file" "$env_file"
    copy_env_for_worktree "$from_dir" "$env_file"
    target_commit="$(git -C "$REPO_DIR" rev-parse "${to_ref}^{commit}")"

    run_scripted_deploy "$from_dir" "$project" "$env_file" "$runtime_dir"
    assert_relay_revision_ready "$project" "$compose_file" "$env_file"
    assert_nginx_running_and_https "$project" "$env_file"
    seed_release_metadata "$runtime_dir" "$(git -C "$from_dir" rev-parse HEAD)" "$target_commit"
    run_scripted_rollback "$from_dir" "$project" "$env_file" "$runtime_dir" "$target_commit"

    if docker compose --project-name "$project" --env-file "$env_file" -f "$compose_file" config --services | grep -qx discovery-relay; then
        assert_relay_revision_ready "$project" "$compose_file" "$env_file"
    else
        assert_old_revision_ready "$project"
    fi
    assert_nginx_running_and_https "$project" "$env_file"
    cleanup_project "$project" "$compose_file" "$env_file"
}

run_rollback_old_then_forward_same_project() {
    local relay_ref="$1"
    local old_ref="$2"
    local name="rollback-old-forward-same-project"
    local relay_dir project env_file runtime_dir old_commit relay_commit compose_file
    add_worktree "$relay_ref" "${name}-relay"
    relay_dir="$ADDED_WORKTREE"
    project="${RUN_ID}-${name}"
    env_file="${WORK_ROOT}/${name}.env"
    runtime_dir="${WORK_ROOT}/${name}-runtime"
    compose_file="${relay_dir}/deploy/docker-compose.yml"
    register_project "$project" "$compose_file" "$env_file"
    copy_env_for_worktree "$relay_dir" "$env_file"
    old_commit="$(git -C "$REPO_DIR" rev-parse "${old_ref}^{commit}")"
    relay_commit="$(git -C "$relay_dir" rev-parse HEAD)"

    run_scripted_deploy "$relay_dir" "$project" "$env_file" "$runtime_dir"
    assert_relay_revision_ready "$project" "$compose_file" "$env_file"
    assert_nginx_running_and_https "$project" "$env_file"
    seed_release_metadata "$runtime_dir" "$relay_commit" "$old_commit"
    run_scripted_rollback "$relay_dir" "$project" "$env_file" "$runtime_dir" "$old_commit"
    assert_old_revision_ready "$project"
    assert_nginx_running_and_https "$project" "$env_file"

    log "checking out relay revision again in the same temporary worktree"
    git -C "$relay_dir" checkout --detach "$relay_commit" >/dev/null
    [[ "$(git -C "$relay_dir" rev-parse HEAD)" == "$relay_commit" ]] \
        || fail "temporary worktree did not return to relay commit ${relay_commit}"

    log "forward deploying relay revision again in the same rolled-back project"
    run_scripted_deploy "$relay_dir" "$project" "$env_file" "$runtime_dir"
    wait_for_service_status "$project" flowstock healthy
    wait_for_service_status "$project" discovery-relay healthy
    assert_relay_revision_ready "$project" "$compose_file" "$env_file"
    assert_no_orphan_relay "$project" 1
    assert_nginx_running_and_https "$project" "$env_file"
    cleanup_project "$project" "$compose_file" "$env_file"
}

run_generic_compose_upgrade() {
    local old_dir relay_dir project runtime_env old_compose relay_compose
    add_worktree "$OLD_REF" "generic-compose-old"
    old_dir="$ADDED_WORKTREE"
    add_worktree "$REF_B" "generic-compose-relay"
    relay_dir="$ADDED_WORKTREE"
    project="${RUN_ID}-generic-compose"
    runtime_env="${relay_dir}/.env"
    old_compose="${old_dir}/deploy/docker-compose.yml"
    relay_compose="${relay_dir}/deploy/docker-compose.yml"
    register_project "$project" "$relay_compose" "$runtime_env"
    copy_env_for_worktree "$relay_dir" "$runtime_env"
    issue_integration_tls_assets "$old_dir"
    issue_integration_tls_assets "$relay_dir"

    log "starting old revision with generic compose up"
    (cd "$old_dir" && cp "$runtime_env" .env && docker compose -p "$project" -f deploy/docker-compose.yml up -d --build --remove-orphans)
    wait_for_service_status "$project" flowstock healthy
    assert_old_revision_ready "$project"
    assert_nginx_running_and_https "$project" "$runtime_env"

    log "upgrading same project with canonical generic compose up"
    (cd "$relay_dir" && docker compose -p "$project" -f deploy/docker-compose.yml up -d --build --remove-orphans)
    wait_for_service_status "$project" flowstock healthy
    wait_for_service_status "$project" discovery-relay healthy
    assert_relay_revision_ready "$project" "$relay_compose" "$runtime_env"
    assert_nginx_running_and_https "$project" "$runtime_env"
    cleanup_project "$project" "$relay_compose" "$runtime_env"
}

run_revision_scenarios() {
    [[ -n "$REF_A" ]] || skip "FLOWSTOCK_DISCOVERY_RELAY_REF_A is required for the full revision suite"
    [[ -n "$REF_B" ]] || fail "FLOWSTOCK_DISCOVERY_RELAY_REF_B must be set"
    run_revision_transition "$OLD_REF" "$REF_B" "upgrade-old-to-relay"
    run_relay_update_transition "$REF_A" "$REF_B" "update-relay-a-to-b"
    run_relay_update_transition "$REF_B" "$REF_A" "update-relay-b-to-a"
    run_rollback_old_then_forward_same_project "$REF_B" "$OLD_REF"
    run_relay_rollback_transition "$REF_B" "$REF_A" "rollback-b-to-a"
    run_generic_compose_upgrade
}

main() {
    ensure_linux_prereqs
    log "using isolated work root ${WORK_ROOT}"
    run_network_scenarios
    run_revision_scenarios
    log "all Linux discovery relay integration scenarios passed"
}

main "$@"
