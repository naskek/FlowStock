#!/usr/bin/env bash
set -euo pipefail

source "$(cd -- "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

require_command python3
# Restore owns preflight; never start/recreate PostgreSQL before validation.
# Preserve canonical base-only Compose. Caller/worker coordination is separate.
export FLOWSTOCK_PROJECT_NAME FLOWSTOCK_ENV_FILE FLOWSTOCK_COMPOSE_FILE
export FLOWSTOCK_RUNTIME_DIR FLOWSTOCK_BACKUP_OUTPUT_DIR
exec python3 "${SCRIPT_DIR}/restore_postgres.py" "$@"
