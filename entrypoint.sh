#!/bin/sh
set -e

DATA_DIR="/app/umbraco/Data"
MEDIA_DIR="/app/wwwroot/media"

mkdir -p "$DATA_DIR" "$MEDIA_DIR" "$CERT_DIR"

# Seed DB om den inte finns i volymen
if [ ! -f "$DATA_DIR/Umbraco.sqlite.db" ] && [ -f "/seed/Umbraco.sqlite.db" ]; then
  echo "Seeding Umbraco SQLite DB..."
  cp /seed/Umbraco.sqlite.db "$DATA_DIR/Umbraco.sqlite.db"
  [ -f /seed/Umbraco.sqlite.db-wal ] && cp /seed/Umbraco.sqlite.db-wal "$DATA_DIR/Umbraco.sqlite.db-wal" || true
  [ -f /seed/Umbraco.sqlite.db-shm ] && cp /seed/Umbraco.sqlite.db-shm "$DATA_DIR/Umbraco.sqlite.db-shm" || true
fi

# Seed media om mappen är tom
if [ -d /seed/media ] && [ -z "$(ls -A "$MEDIA_DIR" 2>/dev/null || true)" ]; then
  echo "Seeding media..."
  cp -R /seed/media/* "$MEDIA_DIR/" 2>/dev/null || true
fi

# Skapa cert (om ditt script skapar /https/aspnetapp.pfx)
# Viktigt: scriptet måste INTE starta appen själv, bara skapa cert.
if [ ! -f "$ASPNETCORE_Kestrel__Certificates__Default__Path" ]; then
  echo "Generating dev cert..."
  /app/generate-cert.sh
fi

# Starta appen
exec dotnet MoveKind.Umbraco.dll
