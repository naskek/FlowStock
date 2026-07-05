#!/usr/bin/env bash
set -euo pipefail

source "$(cd -- "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

usage() {
    cat <<EOF
Usage:
  bash deploy/scripts/rollback_release.sh
  bash deploy/scripts/rollback_release.sh --no-restore
  bash deploy/scripts/rollback_release.sh <git-ref> <dump-path>

Behavior:
  - without arguments rolls back to the previous successful recorded release
  - by default restores the last recorded pre-deploy backup of the current release
  - with --no-restore switches app revision only, without database restore
EOF
}

do_restore="true"
positionals=()

while [[ $# -gt 0 ]]; do
    case "$1" in
        -h|--help)
            usage
            exit 0
            ;;
        --no-restore)
            do_restore="false"
            shift
            ;;
        *)
            positionals+=("$1")
            shift
            ;;
    esac
done

target_input="${positionals[0]:-}"
dump_path="${positionals[1]:-}"
rollback_stamp="$(date -u +%Y%m%dT%H%M%SZ)"
latest_success_file="$(release_state_file latest_success)"
previous_success_file="$(release_state_file previous_success)"
history_file="$(release_history_file "${rollback_stamp}_rollback")"
pre_restore_backup=""
current_commit=""
current_branch=""
target_commit=""
target_ref=""
recorded_current_backup=""
recorded_previous_ref=""
recorded_previous_commit=""

on_error() {
    local exit_code=$?
    log "rollback failed"
    [[ -n "$target_ref" ]] && log "target ref: $target_ref"
    [[ -n "$target_commit" ]] && log "target commit: $target_commit"
    [[ -n "$dump_path" ]] && log "restore dump: $dump_path"
    [[ -n "$pre_restore_backup" ]] && log "pre-restore backup path: $pre_restore_backup"
    compose ps || true
    compose_logs_existing flowstock discovery-relay nginx postgres pgbackup
    exit "$exit_code"
}

trap on_error ERR

ensure_docker
ensure_git_repo
ensure_git_clean_worktree
ensure_compose_config

[[ -f "$latest_success_file" ]] || fail "latest successful release metadata not found: $latest_success_file"

unset deployed_commit deployed_ref backup_path previous_commit previous_branch deploy_kind deployed_at restored_from_dump
# shellcheck disable=SC1090
source "$latest_success_file"
recorded_current_backup="${backup_path:-}"

if [[ -f "$previous_success_file" ]]; then
    unset deployed_commit deployed_ref backup_path previous_commit previous_branch deploy_kind deployed_at restored_from_dump
    # shellcheck disable=SC1090
    source "$previous_success_file"
    recorded_previous_ref="${deployed_ref:-}"
    recorded_previous_commit="${deployed_commit:-}"
fi

if [[ -z "$target_input" ]]; then
    [[ -n "$recorded_previous_commit" ]] || fail "previous successful release metadata not found; pass an explicit git ref"
    target_ref="$recorded_previous_commit"
else
    target_ref="$target_input"
fi

if [[ "$do_restore" == "true" && -z "$dump_path" ]]; then
    dump_path="$recorded_current_backup"
fi

if [[ "$do_restore" == "true" ]]; then
    [[ -n "$dump_path" ]] || fail "no dump path available for restore"
    [[ -f "$dump_path" ]] || fail "dump file not found: $dump_path"
fi

log "fetching git refs from $FLOWSTOCK_GIT_REMOTE"
git_in_repo fetch --prune --tags "$FLOWSTOCK_GIT_REMOTE"

if ! target_commit="$(git_in_repo rev-parse --verify "${target_ref}^{commit}" 2>/dev/null)"; then
    fail "git ref not found: $target_ref"
fi

current_commit="$(git_in_repo rev-parse HEAD)"
current_branch="$(git_in_repo symbolic-ref --short -q HEAD || printf 'detached')"

log "current git revision: $current_commit ($current_branch)"
log "rollback target ref: $target_ref"
log "rollback target commit: $target_commit"

if [[ "$do_restore" == "true" ]]; then
    pre_restore_backup="$(resolve_backup_path "${FLOWSTOCK_BACKUP_OUTPUT_DIR}/rollback_guard/FlowStock_${rollback_stamp}_${current_commit:0:12}.dump")"
fi

remove_discovery_relay_containers
assert_no_discovery_relay_containers
require_udp_port_free 7155

log "checking out detached rollback revision"
git_in_repo checkout --detach "$target_commit"
ensure_compose_config

target_compose_config="$(compose config)"
target_has_relay="false"
if service_exists discovery-relay; then
    target_has_relay="true"
    printf '%s\n' "$target_compose_config" | grep -q "network_mode: host" \
        || fail "target discovery-relay must use host network"
    printf '%s\n' "$target_compose_config" | grep -q "published: \"${FLOWSTOCK_DISCOVERY_BACKEND_PORT}\"" \
        || fail "target flowstock must publish loopback discovery backend"
else
    if ! printf '%s\n' "$target_compose_config" | grep -q "target: 7155" \
        || ! printf '%s\n' "$target_compose_config" | grep -q 'published: "7155"'; then
        fail "target compose does not publish UDP 7155 for flowstock"
    fi
fi

if [[ "$do_restore" == "true" ]]; then
    log "restoring database state from $dump_path"
    export FLOWSTOCK_PRE_RESTORE_BACKUP_PATH_OVERRIDE="$pre_restore_backup"
    bash "${SCRIPT_DIR}/restore_dump.sh" "$dump_path"
    unset FLOWSTOCK_PRE_RESTORE_BACKUP_PATH_OVERRIDE
fi

log "starting application containers for rollback revision"
compose up -d --build --no-deps --force-recreate flowstock
wait_for_flowstock_ready

if [[ "$target_has_relay" == "true" ]]; then
    require_udp_port_free 7155
    check_discovery_backend
    compose up -d --build --no-deps --force-recreate discovery-relay
    wait_for_service_status discovery-relay healthy "$FLOWSTOCK_HEALTH_TIMEOUT_SECONDS"
    require_udp_port_bound 7155
fi

rollback_tail_services=()
while IFS= read -r service; do
    rollback_tail_services+=("$service")
done < <(existing_services nginx pgbackup)
if (( ${#rollback_tail_services[@]} > 0 )); then
    compose up -d --build --no-deps --force-recreate "${rollback_tail_services[@]}"
fi

if service_exists nginx; then
    wait_for_service_status nginx running 60
fi
if [[ "$target_has_relay" == "true" ]]; then
    wait_for_service_status discovery-relay healthy "$FLOWSTOCK_HEALTH_TIMEOUT_SECONDS"
else
    require_udp_port_bound 7155
fi

if [[ -f "$latest_success_file" ]]; then
    cp "$latest_success_file" "$previous_success_file"
fi

write_release_state "$history_file" \
    deployed_at="$rollback_stamp" \
    deploy_kind="rollback" \
    requested_ref="$target_ref" \
    deployed_ref="${recorded_previous_ref:-$target_ref}" \
    deployed_commit="$target_commit" \
    previous_commit="$current_commit" \
    previous_branch="$current_branch" \
    backup_path="$pre_restore_backup" \
    restored_from_dump="${dump_path:-}" \
    git_remote="$FLOWSTOCK_GIT_REMOTE" \
    git_branch="$FLOWSTOCK_GIT_BRANCH"

cp "$history_file" "$latest_success_file"

log "rollback completed successfully"
log "active revision: $target_commit"
if [[ "$do_restore" == "true" ]]; then
    log "restored dump: $dump_path"
    log "pre-rollback safety backup: $pre_restore_backup"
fi
log "release metadata: $history_file"
