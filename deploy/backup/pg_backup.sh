#!/bin/sh
set -eu

BACKUP_DIR="${BACKUP_DIR:-/backups}"
BACKUP_PREFIX="${BACKUP_PREFIX:-FlowStock}"
BACKUP_KEEP="${BACKUP_KEEP:-30}"
BACKUP_INTERVAL_SECONDS="${BACKUP_INTERVAL_SECONDS:-86400}"

mkdir -p "$BACKUP_DIR"

echo "[backup] start: dir=$BACKUP_DIR keep=$BACKUP_KEEP interval=${BACKUP_INTERVAL_SECONDS}s"

while true; do
  ts="$(date +%Y%m%d_%H%M%S)"
  target="$BACKUP_DIR/${BACKUP_PREFIX}_${ts}.dump"
  echo "[backup] running pg_dump -> $target"

  if pg_dump -Fc -f "$target"; then
    echo "[backup] ok $target"
    if [ "$BACKUP_KEEP" -gt 0 ] 2>/dev/null; then
      backups="$(ls -1t "$BACKUP_DIR/${BACKUP_PREFIX}_"*.dump 2>/dev/null || true)"
      if [ -n "$backups" ]; then
        echo "$backups" | tail -n +"$((BACKUP_KEEP + 1))" | while read -r old; do
          [ -n "$old" ] && rm -f "$old"
        done
      fi
    fi
  else
    echo "[backup] pg_dump failed"
    rm -f "$target" || true
  fi

  sleep "$BACKUP_INTERVAL_SECONDS"
done
