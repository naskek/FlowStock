#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="${FLOWSTOCK_REPO_DIR:-$(cd -- "${SCRIPT_DIR}/../.." && pwd)}"
DEPLOY_UPDATE="${REPO_DIR}/deploy/scripts/deploy_update.sh"
ROLLBACK="${REPO_DIR}/deploy/scripts/rollback_release.sh"
COMMON="${REPO_DIR}/deploy/scripts/common.sh"
COMPOSE_FILE="${REPO_DIR}/deploy/docker-compose.yml"
LINUX_INTEGRATION="${REPO_DIR}/tools/integration/discovery-relay-linux.sh"

line_of() {
    local pattern="$1"
    local file="$2"
    grep -n -- "$pattern" "$file" | head -n 1 | cut -d: -f1
}

fail() {
    printf '[flowstock-discovery-script-check] ERROR: %s\n' "$*" >&2
    exit 1
}

require_order() {
    local before_name="$1"
    local before_line="$2"
    local after_name="$3"
    local after_line="$4"
    [[ -n "$before_line" ]] || fail "missing marker: ${before_name}"
    [[ -n "$after_line" ]] || fail "missing marker: ${after_name}"
    (( before_line < after_line )) || fail "expected ${before_name} before ${after_name}"
}

run_check() {
    [[ -f "$DEPLOY_UPDATE" ]] || fail "missing deploy_update.sh"
    [[ -f "$ROLLBACK" ]] || fail "missing rollback_release.sh"
    [[ -f "$COMMON" ]] || fail "missing common.sh"
    [[ -f "$COMPOSE_FILE" ]] || fail "missing docker-compose.yml"
    [[ -f "$LINUX_INTEGRATION" ]] || fail "missing discovery-relay-linux.sh"

    local migrator_line remove_relay_line allow_legacy_port_line start_flowstock_line post_recreate_free_line backend_check_line start_relay_line start_edge_line
    migrator_line="$(line_of '^run_migrator$' "$DEPLOY_UPDATE")"
    remove_relay_line="$(line_of '^remove_discovery_relay_containers$' "$DEPLOY_UPDATE")"
    allow_legacy_port_line="$(line_of '^allow_udp_7155_free_or_current_flowstock_publish$' "$DEPLOY_UPDATE")"
    start_flowstock_line="$(line_of '^compose up -d --no-deps --force-recreate flowstock$' "$DEPLOY_UPDATE")"
    post_recreate_free_line="$(line_of '^require_udp_port_free 7155$' "$DEPLOY_UPDATE")"
    backend_check_line="$(line_of '^check_discovery_backend$' "$DEPLOY_UPDATE")"
    start_relay_line="$(line_of '^    compose up -d --no-deps --force-recreate discovery-relay$' "$DEPLOY_UPDATE")"
    start_edge_line="$(line_of '^compose up -d --no-deps --force-recreate nginx pgbackup$' "$DEPLOY_UPDATE")"

    require_order run_migrator "$migrator_line" remove_discovery_relay_containers "$remove_relay_line"
    require_order remove_discovery_relay_containers "$remove_relay_line" allow_legacy_udp_7155 "$allow_legacy_port_line"
    require_order allow_legacy_udp_7155 "$allow_legacy_port_line" flowstock_recreate "$start_flowstock_line"
    require_order flowstock_recreate "$start_flowstock_line" post_recreate_udp_7155_free "$post_recreate_free_line"
    require_order post_recreate_udp_7155_free "$post_recreate_free_line" check_discovery_backend "$backend_check_line"
    require_order check_discovery_backend "$backend_check_line" discovery_relay_recreate "$start_relay_line"
    require_order discovery_relay_recreate "$start_relay_line" edge_recreate "$start_edge_line"

    local flowstock_recreate_count
    flowstock_recreate_count="$(grep -c '^compose up -d --no-deps --force-recreate flowstock$' "$DEPLOY_UPDATE")"
    [[ "$flowstock_recreate_count" == "1" ]] || fail "deploy_update.sh must recreate flowstock exactly once"

    grep -q '^remove_discovery_relay_containers$' "$ROLLBACK" \
        || fail "rollback_release.sh must remove discovery-relay before checkout"
    grep -q 'target_has_relay="true"' "$ROLLBACK" \
        || fail "rollback_release.sh must branch target revisions with discovery-relay"
    grep -q 'target compose does not publish UDP 7155 for flowstock' "$ROLLBACK" \
        || fail "rollback_release.sh must validate old target UDP 7155 publish"
    grep -q 'docker stop -t 5 "\$container_id" >/dev/null' "$COMMON" \
        || fail "remove_discovery_relay_containers must gracefully stop relay with docker stop -t 5"
    grep -q 'discovery-relay was killed during graceful stop' "$COMMON" \
        || fail "remove_discovery_relay_containers must reject SIGKILL exit code 137"
    grep -q '^allow_udp_7155_free_or_current_flowstock_publish()' "$COMMON" \
        || fail "common.sh must allow legacy flowstock UDP 7155 publish before recreate"
    grep -q 'docker port "\$flowstock_id" 7155/udp' "$COMMON" \
        || fail "legacy UDP 7155 allowance must verify current flowstock published port"
    grep -q 'UDP 7155 is bound by an unexpected owner' "$COMMON" \
        || fail "legacy UDP 7155 allowance must reject unexpected owners"
    grep -q 'ASYNC_CLIENT_PID="\$!"' "$LINUX_INTEGRATION" \
        || fail "Linux graceful-stop client must store the real background PID"
    if grep -q 'client_pid="\$(namespace_client_async_waiting_for_response' "$LINUX_INTEGRATION"; then
        fail "Linux graceful-stop client must not run in command substitution"
    fi
    grep -q 'stop_elapsed_ms < 5000' "$LINUX_INTEGRATION" \
        || fail "Linux graceful-stop scenario must stop relay well before backend timeout"
    grep -q 'git -C "\$relay_dir" checkout --detach "\$relay_commit"' "$LINUX_INTEGRATION" \
        || fail "rollback-old-forward scenario must checkout relay commit in the same worktree"
    grep -q 'temporary worktree did not return to relay commit' "$LINUX_INTEGRATION" \
        || fail "rollback-old-forward scenario must confirm relay HEAD before forward deploy"
    grep -q 'ADDED_WORKTREE="\$path"' "$LINUX_INTEGRATION" \
        || fail "add_worktree must return path through main-shell state"
    grep -q 'WORKTREES+=("\$path")' "$LINUX_INTEGRATION" \
        || fail "add_worktree must register every temporary worktree in the main shell"
    if grep -Eq '="\$\(add_worktree ' "$LINUX_INTEGRATION"; then
        fail "add_worktree must not be called through command substitution"
    fi
    grep -q 'git -C "\$REPO_DIR" worktree remove --force "\$worktree"' "$LINUX_INTEGRATION" \
        || fail "temporary worktree cleanup must use git worktree remove"
    grep -q 'git -C "\$REPO_DIR" worktree list --porcelain' "$LINUX_INTEGRATION" \
        || fail "temporary worktree cleanup must verify git metadata is removed"
    if grep -q '^FLOWSTOCK_TLS_CERT_DIR=' "$LINUX_INTEGRATION"; then
        fail "integration env must not override FLOWSTOCK_TLS_CERT_DIR outside the worktree-mounted cert directory"
    fi
    grep -q 'local cert_dir="\${worktree}/deploy/nginx/certs"' "$LINUX_INTEGRATION" \
        || fail "integration TLS assets must be issued into each worktree deploy/nginx/certs mount"
    grep -q 'issue_integration_tls_assets "\$old_dir"' "$LINUX_INTEGRATION" \
        || fail "generic compose old revision must prepare mounted TLS assets before nginx starts"
    grep -q 'issue_integration_tls_assets "\$relay_dir"' "$LINUX_INTEGRATION" \
        || fail "generic compose relay revision must prepare mounted TLS assets before nginx starts"
    grep -q 'assert_service_strict_status "\$project" discovery-relay healthy' "$LINUX_INTEGRATION" \
        || fail "assert_relay_revision_ready must require strictly healthy discovery-relay"
    if grep -q '\[\[ "\$status" == "healthy" || "\$status" == "running" \]\]' "$LINUX_INTEGRATION"; then
        fail "assert_relay_revision_ready must not accept plain running discovery-relay"
    fi
    grep -q '^assert_nginx_running_and_https()' "$LINUX_INTEGRATION" \
        || fail "Linux integration must verify nginx is running and HTTPS endpoint responds"
    grep -q 'assert_project_has_no_volumes_or_networks' "$LINUX_INTEGRATION" \
        || fail "Linux integration cleanup must assert temporary volumes and networks are removed"
    if awk '
        /^remove_discovery_relay_containers\(\) \{/ { in_func=1 }
        in_func && /^\}/ { in_func=0 }
        in_func && /docker rm -f/ { found=1 }
        END { exit found ? 0 : 1 }
    ' "$COMMON"; then
        fail "production relay removal must not use docker rm -f"
    fi
    if awk '
        $1 == "discovery-relay:" { in_relay=1 }
        in_relay && $1 == "timeout:" && $2 == "12s" { found=1 }
        in_relay && $1 == "nginx:" { in_relay=0 }
        END { exit found ? 0 : 1 }
    ' "$COMPOSE_FILE"; then
        :
    else
        fail "discovery-relay Docker healthcheck timeout must be 12s"
    fi

    printf '[flowstock-discovery-script-check] ok\n'
}

run_check "$@"
