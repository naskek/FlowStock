#!/usr/bin/env bash
set -euo pipefail

source "$(cd -- "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

usage() {
    cat <<EOF
Usage:
  bash deploy/scripts/issue_server_cert.sh

Behavior:
  - issues a server certificate from the local root CA
  - writes cert/key into deploy/nginx/certs by default
EOF
}

if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    usage
    exit 0
fi

require_command openssl
[[ -n "$FLOWSTOCK_TLS_SERVER_NAME" ]] || fail "FLOWSTOCK_TLS_SERVER_NAME must not be empty"
require_file "$FLOWSTOCK_CA_CERT_PATH"
require_file "$FLOWSTOCK_CA_KEY_PATH"
mkdir -p "$FLOWSTOCK_TLS_CERT_DIR"

server_sans="$(normalized_tls_sans)"
work_dir="$(mktemp -d)"
key_tmp="${work_dir}/flowstock.key"
csr_tmp="${work_dir}/flowstock.csr"
cert_tmp="${work_dir}/flowstock.crt"
config_tmp="${work_dir}/openssl.cnf"

cleanup() {
    rm -rf "$work_dir"
}

trap cleanup EXIT

cat >"$config_tmp" <<EOF
[ req ]
prompt = no
default_bits = 2048
default_md = sha256
distinguished_name = dn
req_extensions = req_ext

[ dn ]
CN = ${FLOWSTOCK_TLS_SERVER_NAME}

[ req_ext ]
subjectAltName = ${server_sans}
extendedKeyUsage = serverAuth
keyUsage = digitalSignature, keyEncipherment
EOF

openssl genrsa -out "$key_tmp" 2048
openssl req -new -key "$key_tmp" -out "$csr_tmp" -config "$config_tmp"
openssl x509 -req -sha256 \
    -in "$csr_tmp" \
    -CA "$FLOWSTOCK_CA_CERT_PATH" \
    -CAkey "$FLOWSTOCK_CA_KEY_PATH" \
    -CAserial "$FLOWSTOCK_CA_SERIAL_PATH" \
    -CAcreateserial \
    -out "$cert_tmp" \
    -days "$FLOWSTOCK_SERVER_CERT_DAYS" \
    -copy_extensions copy

install -m 600 "$key_tmp" "$FLOWSTOCK_TLS_KEY_PATH"
install -m 644 "$cert_tmp" "$FLOWSTOCK_TLS_CERT_PATH"

log "server certificate issued"
log "server name: $FLOWSTOCK_TLS_SERVER_NAME"
log "subjectAltName: $server_sans"
log "cert path: $FLOWSTOCK_TLS_CERT_PATH"
log "key path: $FLOWSTOCK_TLS_KEY_PATH"
