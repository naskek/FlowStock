#!/usr/bin/env bash
set -euo pipefail

source "$(cd -- "$(dirname "${BASH_SOURCE[0]}")" && pwd)/common.sh"

usage() {
    cat <<EOF
Usage:
  bash deploy/scripts/renew_server_cert.sh
  bash deploy/scripts/renew_server_cert.sh --force

Behavior:
  - in local_ca mode reissues the server cert when it is missing, invalid, mismatched, or close to expiry
  - with --force always reissues the server cert
EOF
}

force="false"

while [[ $# -gt 0 ]]; do
    case "$1" in
        --force)
            force="true"
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            fail "unknown argument: $1"
            ;;
    esac
done

require_command openssl
[[ "$FLOWSTOCK_TLS_MODE" == "local_ca" ]] || fail "renew_server_cert.sh requires FLOWSTOCK_TLS_MODE=local_ca"
[[ -n "$FLOWSTOCK_TLS_SERVER_NAME" ]] || fail "FLOWSTOCK_TLS_SERVER_NAME must not be empty"
require_file "$FLOWSTOCK_CA_CERT_PATH"
require_file "$FLOWSTOCK_CA_KEY_PATH"
mkdir -p "$FLOWSTOCK_TLS_CERT_DIR"

expected_sans="$(normalized_tls_sans)"
renew_after_seconds=$(( FLOWSTOCK_SERVER_CERT_RENEW_BEFORE_DAYS * 86400 ))
needs_issue="false"

certificate_sans() {
    openssl x509 -in "$FLOWSTOCK_TLS_CERT_PATH" -noout -ext subjectAltName 2>/dev/null \
        | tail -n +2 \
        | tr -d ' \n' \
        | sed 's/IPAddress:/IP:/g'
}

if [[ "$force" == "true" ]]; then
    needs_issue="true"
elif [[ ! -f "$FLOWSTOCK_TLS_CERT_PATH" || ! -f "$FLOWSTOCK_TLS_KEY_PATH" ]]; then
    log "server cert or key is missing; issuing a new certificate"
    needs_issue="true"
elif ! openssl verify -CAfile "$FLOWSTOCK_CA_CERT_PATH" "$FLOWSTOCK_TLS_CERT_PATH" >/dev/null 2>&1; then
    log "server cert is not trusted by the configured local CA; issuing a new certificate"
    needs_issue="true"
elif ! openssl x509 -in "$FLOWSTOCK_TLS_CERT_PATH" -noout -subject -nameopt RFC2253 2>/dev/null | grep -Fq "CN=${FLOWSTOCK_TLS_SERVER_NAME}"; then
    log "server cert common name does not match FLOWSTOCK_TLS_SERVER_NAME; issuing a new certificate"
    needs_issue="true"
elif ! openssl x509 -checkend "$renew_after_seconds" -noout -in "$FLOWSTOCK_TLS_CERT_PATH" >/dev/null 2>&1; then
    log "server cert expires within ${FLOWSTOCK_SERVER_CERT_RENEW_BEFORE_DAYS} days; issuing a new certificate"
    needs_issue="true"
else
    actual_sans="$(certificate_sans)"
    IFS=',' read -r -a sans_list <<< "$expected_sans"
    for san in "${sans_list[@]}"; do
        if [[ ",${actual_sans}," != *",${san},"* ]]; then
            log "server cert SAN list does not match expected settings; issuing a new certificate"
            needs_issue="true"
            break
        fi
    done
fi

if [[ "$needs_issue" == "true" ]]; then
    bash "${SCRIPT_DIR}/issue_server_cert.sh"
else
    log "server cert is present and still valid"
fi
