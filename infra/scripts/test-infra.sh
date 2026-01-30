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

MODEL_FILE="${LLM_MODEL_FILE:-${LLM_MODEL:-}}"
if [[ -z "$MODEL_FILE" ]]; then
  echo "ERROR: LLM_MODEL_FILE (or LLM_MODEL) not set in infra/.env"
  exit 1
fi

if [[ ! -f "$ROOT_DIR/models/$MODEL_FILE" ]]; then
  echo "ERROR: model file missing: models/$MODEL_FILE"
  echo "Run:"
  echo "  bash infra/scripts/download-model.sh"
  exit 1
fi

echo "== Checking Qdrant =="
curl -fsS "http://127.0.0.1:${QDRANT_HOST_PORT:-6333}/readyz" >/dev/null
echo "OK: Qdrant /readyz"

echo "== Checking TEI =="
curl -fsS "http://127.0.0.1:${TEI_HOST_PORT:-8081}/health" >/dev/null
echo "OK: TEI /health"

echo "== Checking llama.cpp server (OpenAI chat) =="
curl -fsS "http://127.0.0.1:${LLM_HOST_PORT:-1234}/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -d '{"model":"local","messages":[{"role":"user","content":"Réponds uniquement: OK"}],"temperature":0,"stream":false,"max_tokens":5}' \
  | grep -q '"choices"'
echo "OK: llama /v1/chat/completions"

echo "== Checking Postgres (pg_isready) =="
docker compose -f "$ROOT_DIR/infra/docker-compose.yml" --env-file "$ENV_FILE" exec -T postgres \
  pg_isready -U "${POSTGRES_USER}" -d "${POSTGRES_DB}" >/dev/null
echo "OK: Postgres pg_isready"

echo ""
echo "All infra checks passed."
