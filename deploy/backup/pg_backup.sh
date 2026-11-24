#!/bin/sh
set -eu

# The durable scheduler preserves unmanaged legacy FlowStock_*.dump archives.
BACKUP_DIR="${BACKUP_DIR:-/backups}"
BACKUP_EXPECTED_BYTES="${BACKUP_EXPECTED_BYTES:-1073741824}"
BACKUP_LOCK_TIMEOUT_SECONDS="${BACKUP_LOCK_TIMEOUT_SECONDS:-10}"
MANAGER="/usr/local/bin/managed_backup.py"
SCHEDULER="/usr/local/bin/backup_scheduler.py"

# A legacy container can see this NEW script through its old bind mount.
# Never turn a permanent image incompatibility into an apparently running loop.
runtime_error() {
  echo "[backup] INCOMPATIBLE_BACKUP_RUNTIME: $*; keep worker stopped and recover via canonical deploy; do not docker start the legacy container" >&2
  exit 78
}
command -v python3 >/dev/null 2>&1 || runtime_error 'python3 is missing'
[ -s "$MANAGER" ] && [ -r "$MANAGER" ] || runtime_error 'managed_backup.py is missing'
python3 "$MANAGER" --help >/dev/null 2>&1 || runtime_error 'managed_backup.py cannot execute'
[ -s "$SCHEDULER" ] && [ -r "$SCHEDULER" ] || runtime_error 'backup_scheduler.py is missing'
python3 "$SCHEDULER" --help >/dev/null 2>&1 || runtime_error 'backup_scheduler.py cannot execute'
for client in pg_dump pg_restore; do
  version="$("$client" --version 2>/dev/null)" || runtime_error "$client is missing"
  case "$version" in
    *"(PostgreSQL) 16."*) ;;
    *) runtime_error "$client must be PostgreSQL 16" ;;
  esac
done
case "${1:-}" in
  --check-runtime) exit 0 ;;
  '') ;;
  *) runtime_error 'unsupported worker argument' ;;
esac

case "$BACKUP_EXPECTED_BYTES" in
  ''|*[!0-9]*) echo "[backup] invalid BACKUP_EXPECTED_BYTES" >&2; exit 1 ;;
esac
[ "$BACKUP_EXPECTED_BYTES" -gt 0 ] || { echo "[backup] estimate must be positive" >&2; exit 1; }
[ -d "$BACKUP_DIR" ] || { echo "[backup] backup root missing" >&2; exit 1; }

echo "[backup] durable managed worker: root=$BACKUP_DIR; enable through operator config after cutover"
exec python3 "$SCHEDULER" --root "$BACKUP_DIR" \
  --lock-timeout "$BACKUP_LOCK_TIMEOUT_SECONDS" --expected-bytes "$BACKUP_EXPECTED_BYTES" run
