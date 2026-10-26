#!/bin/sh
set -eu

# Preserve legacy FlowStock_*.dump archives. Managed retention is Issue #123.
BACKUP_DIR="${BACKUP_DIR:-/backups}"
BACKUP_INTERVAL_SECONDS="${BACKUP_INTERVAL_SECONDS:-86400}"
BACKUP_EXPECTED_BYTES="${BACKUP_EXPECTED_BYTES:-1073741824}"
BACKUP_LOCK_TIMEOUT_SECONDS="${BACKUP_LOCK_TIMEOUT_SECONDS:-10}"
MANAGER="/usr/local/bin/managed_backup.py"

case "$BACKUP_INTERVAL_SECONDS" in
  ''|*[!0-9]*) echo "[backup] invalid BACKUP_INTERVAL_SECONDS" >&2; exit 1 ;;
esac
case "$BACKUP_EXPECTED_BYTES" in
  ''|*[!0-9]*) echo "[backup] invalid BACKUP_EXPECTED_BYTES" >&2; exit 1 ;;
esac
[ "$BACKUP_INTERVAL_SECONDS" -gt 0 ] || { echo "[backup] interval must be positive" >&2; exit 1; }
[ "$BACKUP_EXPECTED_BYTES" -gt 0 ] || { echo "[backup] estimate must be positive" >&2; exit 1; }
[ -d "$BACKUP_DIR" ] || { echo "[backup] backup root missing" >&2; exit 1; }

echo "[backup] managed worker: root=$BACKUP_DIR interval=${BACKUP_INTERVAL_SECONDS}s"
while :; do
  # Pause can race the read: create checks it again under the same shared flock.
  if status="$(python3 "$MANAGER" --root "$BACKUP_DIR" \
      --lock-timeout "$BACKUP_LOCK_TIMEOUT_SECONDS" status)"; then
    if printf '%s\n' "$status" | grep -Eq '"paused"[[:space:]]*:[[:space:]]*true'; then
      echo "[backup] maintenance paused; scheduled dump skipped"
    elif ! python3 "$MANAGER" --root "$BACKUP_DIR" \
        --lock-timeout "$BACKUP_LOCK_TIMEOUT_SECONDS" create \
        --origin scheduled --expected-bytes "$BACKUP_EXPECTED_BYTES"; then
      echo "[backup] managed scheduled dump failed; existing archives preserved" >&2
    fi
  else
    echo "[backup] unable to read shared maintenance state; refusing unsafe dump" >&2
  fi
  sleep "$BACKUP_INTERVAL_SECONDS"
done
