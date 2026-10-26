#!/usr/bin/env bash
set -euo pipefail
scripts="$(cd -- "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
fixture="$(mktemp -d)"
trap 'jobs -pr | xargs -r kill 2>/dev/null || true; rm -rf -- "$fixture"' EXIT
export FLOWSTOCK_ENV_FILE="$fixture/nonexistent.env"
export FLOWSTOCK_COORDINATION_DIR="$fixture/host-maintenance"
export FLOWSTOCK_REPO_DIR="$fixture"
export FLOWSTOCK_COMPOSE_FILE="$fixture/missing.yml"
export FLOWSTOCK_RUNTIME_DIR="$fixture/runtime"
export FLOWSTOCK_BACKUP_OUTPUT_DIR="$fixture/backups"
export SCRIPTS="$scripts"
(
  source "$SCRIPTS/common.sh"
  acquire_host_backup_lock
  printf locked >"$FLOWSTOCK_COORDINATION_DIR/owner.ready"
  sleep 4
) &
first=$!
for _ in $(seq 1 60); do
  [[ -f "$FLOWSTOCK_COORDINATION_DIR/owner.ready" ]] && break
  sleep 0.05
done
[[ -f "$FLOWSTOCK_COORDINATION_DIR/owner.ready" ]] || { echo "first caller never obtained host lock" >&2; exit 1; }
if timeout 1 bash -c 'source "$SCRIPTS/common.sh"; acquire_host_backup_lock' 2>/dev/null; then
  echo "parallel operator entered maintenance critical section" >&2
  exit 1
fi
wait "$first"
bash -c 'source "$SCRIPTS/common.sh"; acquire_host_backup_lock'
[[ "$(stat -c %a "$FLOWSTOCK_COORDINATION_DIR")" == 700 ]]
[[ "$(stat -c %a "$FLOWSTOCK_COORDINATION_DIR/operator.lock")" == 600 ]]
rm "$FLOWSTOCK_COORDINATION_DIR/operator.lock"
ln -s "$fixture/not-a-real-lock" "$FLOWSTOCK_COORDINATION_DIR/operator.lock"
if bash -c 'source "$SCRIPTS/common.sh"; acquire_host_backup_lock' 2>/dev/null; then
  echo "unsafe symlink lock accepted" >&2
  exit 1
fi
echo "host operator/deploy/restore flock concurrency regression PASS"
