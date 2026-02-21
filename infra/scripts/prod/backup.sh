#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
ENV_FILE="$ROOT/infra/.env"
COMPOSE_FILE="$ROOT/infra/docker-compose.prod.yml"

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

TS="$(date +%Y%m%d_%H%M%S)"
OUT_DIR="${1:-$INSTALL_ROOT/backups/$TS}"
mkdir -p "$OUT_DIR"

# Postgres
docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" exec -T postgres sh -lc \
  "PGPASSWORD=\"$POSTGRES_PASSWORD\" pg_dump -U ${POSTGRES_USER:-saaia} -d ${POSTGRES_DB:-saaia}" \
  > "$OUT_DIR/postgres_dump.sql"

# Qdrant archive
QID="$(docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" ps -q qdrant)"
docker run --rm --volumes-from "$QID" -v "$OUT_DIR:/backup" alpine:3.20 sh -lc \
  "tar -czf /backup/qdrant_storage.tgz -C /qdrant/storage ."

# deploy copy (from install root)
cp -a "$INSTALL_ROOT/deploy" "$OUT_DIR/deploy" 2>/dev/null || true

echo "DONE => $OUT_DIR"
