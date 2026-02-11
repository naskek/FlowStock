#!/bin/sh
set -eu

IP="${1:-192.168.1.34}"
SCRIPT_DIR="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
OUT_DIR="${SCRIPT_DIR}/certs"

mkdir -p "$OUT_DIR"

openssl req -x509 -newkey rsa:2048 -nodes -sha256 -days 825 \
  -keyout "${OUT_DIR}/flowstock.key" \
  -out "${OUT_DIR}/flowstock.crt" \
  -subj "/CN=${IP}" \
  -addext "subjectAltName=IP:${IP}"

echo "Created ${OUT_DIR}/flowstock.crt and ${OUT_DIR}/flowstock.key for ${IP}"
