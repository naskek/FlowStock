#!/usr/bin/env bash
set -euo pipefail

source "$(cd -- "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

usage() {
    cat <<EOF
Usage:
  bash deploy/scripts/backfill_marking_status.sh --created-before YYYY-MM-DD [--dry-run]
  bash deploy/scripts/backfill_marking_status.sh --created-before YYYY-MM-DD --apply --confirm APPLY

Default mode is dry-run. Run a fresh backup before --apply.
This wrapper runs the maintenance command inside Docker Compose; host dotnet is not required.
EOF
}

created_before=""
apply=false
confirm=""
pass_args=()

if [[ $# -eq 0 ]]; then
    usage >&2
    exit 2
fi

while [[ $# -gt 0 ]]; do
    case "$1" in
        --created-before)
            if [[ $# -lt 2 || "${2:-}" == --* ]]; then
                printf '[flowstock] ERROR: --created-before requires YYYY-MM-DD\n' >&2
                usage >&2
                exit 2
            fi
            created_before="$2"
            pass_args+=("--created-before" "$2")
            shift 2
            ;;
        --dry-run)
            apply=false
            pass_args+=("--dry-run")
            shift
            ;;
        --apply)
            apply=true
            pass_args+=("--apply")
            shift
            ;;
        --confirm)
            if [[ $# -lt 2 || "${2:-}" == --* ]]; then
                printf '[flowstock] ERROR: --confirm requires a value\n' >&2
                usage >&2
                exit 2
            fi
            confirm="$2"
            pass_args+=("--confirm" "$2")
            shift 2
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            printf '[flowstock] ERROR: unknown argument: %s\n' "$1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if [[ -z "$created_before" ]]; then
    printf '[flowstock] ERROR: --created-before YYYY-MM-DD is required\n' >&2
    usage >&2
    exit 2
fi

if [[ ! "$created_before" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}$ ]]; then
    printf '[flowstock] ERROR: --created-before must use YYYY-MM-DD format\n' >&2
    usage >&2
    exit 2
fi

if [[ "$apply" == true && "$confirm" != "APPLY" ]]; then
    printf '[flowstock] ERROR: apply mode requires --confirm APPLY\n' >&2
    usage >&2
    exit 2
fi

if [[ "$apply" == false && -n "$confirm" ]]; then
    printf '[flowstock] ERROR: --confirm is only valid together with --apply\n' >&2
    usage >&2
    exit 2
fi

ensure_docker
ensure_compose_config
ensure_postgres_healthy
run_migrator

log "building flowstock maintenance image"
compose build flowstock

if [[ "$apply" == true ]]; then
    log "running marking status backfill (APPLY, created_before=${created_before})"
else
    log "running marking status backfill (DRY-RUN, created_before=${created_before})"
fi

compose run --rm --no-deps flowstock maintenance backfill-marking-status "${pass_args[@]}"
