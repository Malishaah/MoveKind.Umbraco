#!/usr/bin/env bash
set -euo pipefail

CERT_DIR=${CERT_DIR:-/https}
CERT_NAME=${CERT_NAME:-aspnetapp}
CERT_PATH="$CERT_DIR/${CERT_NAME}.pfx"
CERT_PASSWORD=${CERT_PASSWORD:-pass123!}
CERT_DAYS=${CERT_DAYS:-365}
CERT_CN=${CERT_CN:-localhost}

mkdir -p "$CERT_DIR"

if [ ! -f "$CERT_PATH" ]; then
  echo "🛠 Generating self-signed cert ($CERT_CN) -> $CERT_PATH"
  openssl req -x509 -newkey rsa:2048 -nodes \
    -keyout "$CERT_DIR/${CERT_NAME}.key" \
    -out    "$CERT_DIR/${CERT_NAME}.crt" \
    -days "$CERT_DAYS" \
    -subj "/CN=${CERT_CN}"

  openssl pkcs12 -export \
    -inkey "$CERT_DIR/${CERT_NAME}.key" \
    -in    "$CERT_DIR/${CERT_NAME}.crt" \
    -out   "$CERT_PATH" \
    -password "pass:${CERT_PASSWORD}"
else
  echo "✅ Found existing cert: $CERT_PATH"
fi

echo "➡️  Launching app…"
exec dotnet MoveKind.Umbraco.dll
