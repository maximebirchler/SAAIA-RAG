#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ENV_FILE="$ROOT_DIR/infra/.env"

if [[ ! -f "$ENV_FILE" ]]; then
  echo "ERROR: missing infra/.env (copy infra/.env.example -> infra/.env)"
  exit 1
fi

# Load .env safely (simple key=value)
set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a

echo "== Checking Qdrant =="
curl -fsS "http://127.0.0.1:${QDRANT_HOST_PORT:-6333}/readyz" >/dev/null
echo "OK: Qdrant /readyz"

echo "== Checking TEI =="
curl -fsS "http://127.0.0.1:${TEI_HOST_PORT:-8081}/health" >/dev/null
echo "OK: TEI /health"

echo "== Checking Postgres (pg_isready) =="
docker compose -f "$ROOT_DIR/infra/docker-compose.yml" --env-file "$ENV_FILE" exec -T postgres \
  pg_isready -U "${POSTGRES_USER}" -d "${POSTGRES_DB}" >/dev/null
echo "OK: Postgres pg_isready"

echo ""
echo "All infra checks passed."
