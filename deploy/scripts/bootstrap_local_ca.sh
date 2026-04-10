#!/usr/bin/env bash
set -euo pipefail

source "$(cd -- "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

usage() {
    cat <<EOF
Usage:
  bash deploy/scripts/bootstrap_local_ca.sh

Behavior:
  - creates a local root CA in FLOWSTOCK_CA_DIR if it does not exist
  - keeps an existing CA untouched
EOF
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    usage
    exit 0
fi

require_command openssl
mkdir -p "$FLOWSTOCK_CA_DIR"

if [[ -f "$FLOWSTOCK_CA_CERT_PATH" && -f "$FLOWSTOCK_CA_KEY_PATH" ]]; then
    log "local CA already exists at $FLOWSTOCK_CA_DIR"
    log "CA cert: $FLOWSTOCK_CA_CERT_PATH"
    log "CA key: $FLOWSTOCK_CA_KEY_PATH"
    exit 0
fi

if [[ -f "$FLOWSTOCK_CA_CERT_PATH" || -f "$FLOWSTOCK_CA_KEY_PATH" ]]; then
    fail "local CA is only partially present; remove the incomplete files in $FLOWSTOCK_CA_DIR or restore the missing pair"
fi

log "creating local root CA in $FLOWSTOCK_CA_DIR"
openssl req -x509 -newkey rsa:4096 -nodes -sha256 \
    -days "$FLOWSTOCK_CA_CERT_DAYS" \
    -keyout "$FLOWSTOCK_CA_KEY_PATH" \
    -out "$FLOWSTOCK_CA_CERT_PATH" \
    -subj "/CN=${FLOWSTOCK_CA_COMMON_NAME}"

chmod 600 "$FLOWSTOCK_CA_KEY_PATH"
chmod 644 "$FLOWSTOCK_CA_CERT_PATH"

log "local root CA created"
log "CA cert: $FLOWSTOCK_CA_CERT_PATH"
log "CA key: $FLOWSTOCK_CA_KEY_PATH"
log "install the CA cert on client devices once, then issue server certificates from it"
