#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
ENV_FILE="$ROOT/infra/.env"
COMPOSE_FILE="$ROOT/infra/docker-compose.prod.yml"
IN_DIR="${1:?Usage: restore.sh <backup_dir>}"

set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a

INSTALL_ROOT="${SAAIA_INSTALL_ROOT:-}"
if [[ -z "$INSTALL_ROOT" && -n "${SAAIA_DEPLOY_DIR:-}" && "${SAAIA_DEPLOY_DIR}" = /* ]]; then
  base="$(basename "${SAAIA_DEPLOY_DIR%/}")"
  if [[ "${base,,}" != "deploy" ]]; then
    INSTALL_ROOT="$SAAIA_DEPLOY_DIR"
  else
    INSTALL_ROOT="$(dirname "$SAAIA_DEPLOY_DIR")"
  fi
fi
INSTALL_ROOT="${INSTALL_ROOT:-$ROOT}"

export SAAIA_INSTALL_ROOT="$INSTALL_ROOT"

DUMP="$IN_DIR/postgres_dump.sql"
QTAR="$IN_DIR/qdrant_storage.tgz"

[[ -f "$DUMP" ]] || { echo "Missing $DUMP"; exit 1; }
[[ -f "$QTAR" ]] || { echo "Missing $QTAR"; exit 1; }

docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" down

docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" up -d postgres qdrant

QID="$(docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" ps -q qdrant)"
docker run --rm --volumes-from "$QID" -v "$IN_DIR:/backup" alpine:3.20 sh -lc \
  "rm -rf /qdrant/storage/* && tar -xzf /backup/qdrant_storage.tgz -C /qdrant/storage"

PID="$(docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" ps -q postgres)"
docker cp "$DUMP" "$PID:/tmp/restore.sql"
docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" exec -T postgres sh -lc \
  "PGPASSWORD=\"$POSTGRES_PASSWORD\" psql -U ${POSTGRES_USER:-saaia} -d ${POSTGRES_DB:-saaia} -f /tmp/restore.sql"

docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" up -d --build
