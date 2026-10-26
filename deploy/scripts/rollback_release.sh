#!/usr/bin/env bash
set -euo pipefail

source "$(cd -- "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

usage() {
    cat <<EOF
Usage:
  bash deploy/scripts/rollback_release.sh --no-restore <exact-git-commit>
  bash deploy/scripts/rollback_release.sh --no-restore --allow-downlevel-app-only <exact-git-commit>

Behavior:
  - requires an explicit exact target commit; stale latest_success/previous_success
    pointers are never used as source identity after canonical PowerShell deploy
  - database restore + restart is NOT automatic: restore_dump.sh requires explicit
    recovery validation and service restart gate; select target/archive manually
EOF
}

do_restore="true"
allow_downlevel="false"
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
        --allow-downlevel-app-only)
            allow_downlevel="true"
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
# Validate caller intent BEFORE Docker, git or checkout access. Canonical
# production deploy never updates historical latest_success/previous_success.
(( ${#positionals[@]} == 1 )) || fail "rollback requires exactly one explicit target commit"
[[ "$target_input" =~ ^[0-9a-f]{40}$ ]] || fail "rollback target must be an exact 40-character lowercase commit SHA"
[[ "$do_restore" == "false" ]] || fail "automatic SQL restore/restart is unsafe; use explicit restore_dump.sh, validate recovery, then a separate app restart"
rollback_stamp="$(date -u +%Y%m%dT%H%M%SZ)"
latest_success_file="$(release_state_file latest_success)"
previous_success_file="$(release_state_file previous_success)"
history_file="$(release_history_file "${rollback_stamp}_rollback")"
pre_restore_backup=""
current_commit=""
current_branch=""
target_commit=""
target_ref=""
target_supports_source_identity="false"
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

acquire_host_backup_lock
ensure_docker
ensure_git_repo
ensure_git_clean_worktree
ensure_compose_config

# Canonical deploy does not update legacy release pointers. They can refer
# to a different checkout/database and must not imply rollback provenance.
target_ref="$target_input"
if [[ -e "${FLOWSTOCK_RUNTIME_DIR}/restore/active.json" || -L "${FLOWSTOCK_RUNTIME_DIR}/restore/active.json" ]]; then
    fail "restore maintenance active; app-only rollback forbidden"
fi

log "fetching git refs from $FLOWSTOCK_GIT_REMOTE"
git_in_repo fetch --prune --tags "$FLOWSTOCK_GIT_REMOTE"

if ! target_commit="$(git_in_repo rev-parse --verify "${target_ref}^{commit}" 2>/dev/null)"; then
    fail "git ref not found: $target_ref"
fi

# Downlevel source cannot honour managed pause. Explicitly allow only
# app-only switch after external schema/data compatibility qualification.
# The legacy backup worker is NEVER started by this rollback workflow.
target_compose="$(git_in_repo show "$target_commit:deploy/docker-compose.yml")" ||
    fail "rollback target Compose unavailable"
legacy_worker_target="false"
if ! git_in_repo cat-file -e "$target_commit:deploy/backup/Dockerfile" 2>/dev/null ||
    ! printf '%s\n' "$target_compose" | grep -q 'dockerfile: deploy/backup/Dockerfile'; then
    legacy_worker_target="true"
    [[ "$allow_downlevel" == true ]] ||
        fail "downlevel worker unsafe; explicit --allow-downlevel-app-only and operator compatibility review required"
    log "downlevel app-only target; legacy pgbackup must remain stopped"
else
    [[ "$allow_downlevel" == false ]] ||
        fail "downlevel override is forbidden for managed backup target"
fi

current_commit="$(git_in_repo rev-parse HEAD)"
current_branch="$(git_in_repo symbolic-ref --short -q HEAD || printf 'detached')"

log "current git revision: $current_commit ($current_branch)"
log "rollback target ref: $target_ref"
log "rollback target commit: $target_commit"

if target_supports_source_identity_contract "$target_commit"; then
    target_supports_source_identity="true"
    log "rollback target supports source identity contract"
else
    log "rollback target predates source identity contract"
fi

if [[ "$do_restore" == "true" ]]; then
    pre_restore_backup="$(resolve_backup_path "${FLOWSTOCK_BACKUP_OUTPUT_DIR}/rollback_guard/FlowStock_${rollback_stamp}_${current_commit:0:12}.dump")"
fi

if [[ "$legacy_worker_target" == true ]]; then
    old_backup_id="$(compose ps -q pgbackup)"
    if [[ -n "$old_backup_id" ]]; then
        # prevent Docker engine restart from reviving an old worker that does
        # not understand the paused managed catalog.
        docker update --restart=no "$old_backup_id" >/dev/null
        compose stop pgbackup
        [[ "$(docker inspect -f '{{.State.Status}}' "$old_backup_id")" == exited ]] ||
            fail "downlevel pgbackup cannot be proven stopped"
    fi
fi

remove_discovery_relay_containers
assert_no_discovery_relay_containers
require_udp_port_free 7155

log "checking out detached rollback revision"
git_in_repo checkout --detach "$target_commit"
export_source_commit_from_checkout
ensure_compose_config

if telegram_enabled && [[ -f "$FLOWSTOCK_TELEGRAM_COMPOSE_FILE" ]]; then
    ensure_telegram_deploy_prerequisites
    compose_with_telegram config -q
    compose() {
        compose_with_telegram "$@"
    }
    log "Telegram overlay enabled for rollback target"
elif telegram_enabled; then
    log "WARNING: rollback target does not contain Telegram overlay; continuing base-only"
fi

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
if [[ "$target_supports_source_identity" == "true" ]]; then
    assert_deployed_source_commit
else
    assert_deployed_legacy_version_payload
    log "WARNING: rollback target predates source identity contract; exact running source commit cannot be confirmed through /api/version"
fi

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
done < <(if [[ "$legacy_worker_target" == true ]]; then
    existing_services nginx
else
    existing_services nginx pgbackup
fi)
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
    deployed_ref="$target_ref" \
    deployed_commit="$target_commit" \
    previous_commit="$current_commit" \
    previous_branch="$current_branch" \
    backup_path="$pre_restore_backup" \
    restored_from_dump="${dump_path:-}" \
    git_remote="$FLOWSTOCK_GIT_REMOTE" \
    git_branch="$FLOWSTOCK_GIT_BRANCH"

cp "$history_file" "$latest_success_file"

if [[ "$legacy_worker_target" == true ]]; then
    log "WARNING: downlevel pgbackup stopped with restart=no; manual restore compatibility/recovery gate required"
fi
log "rollback completed successfully"
log "active revision: $target_commit"
if [[ "$do_restore" == "true" ]]; then
    log "restored dump: $dump_path"
    log "pre-rollback safety backup: $pre_restore_backup"
fi
log "release metadata: $history_file"
