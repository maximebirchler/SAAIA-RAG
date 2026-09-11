#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../../.." && pwd)"
ENV_FILE="$ROOT/infra/.env"
ENV_EXAMPLE="$ROOT/infra/.env.example"
COMPOSE_FILE="$ROOT/infra/docker-compose.prod.yml"
TEMPLATE="$ROOT/infra/config/deployment.config.prod.template.json"

if [[ ! -f "$ENV_FILE" ]]; then
  if [[ -f "$ENV_EXAMPLE" ]]; then
    cp "$ENV_EXAMPLE" "$ENV_FILE"
    echo "Created infra/.env from .env.example. Please edit it then re-run." >&2
    exit 1
  fi
  echo "Missing infra/.env (and no .env.example found)." >&2
  exit 1
fi

set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a

: "${POSTGRES_PASSWORD:?POSTGRES_PASSWORD is required}"
: "${SAAIA_AUTH_PEPPER:?SAAIA_AUTH_PEPPER is required}"
: "${SAAIA_CONFIG_PRIVATE_KEY_PATH:?SAAIA_CONFIG_PRIVATE_KEY_PATH is required}"

BOOTSTRAP_ENABLED_RAW="${SAAIA_BOOTSTRAP_ENABLED:-true}"
BOOTSTRAP_ENABLED="true"
if [[ "${BOOTSTRAP_ENABLED_RAW,,}" =~ ^(0|false|no|off)$ ]]; then
  BOOTSTRAP_ENABLED="false"
fi

if [[ "$BOOTSTRAP_ENABLED" == "true" ]]; then
  : "${SAAIA_BOOTSTRAP_API_KEY:?SAAIA_BOOTSTRAP_API_KEY is required when SAAIA_BOOTSTRAP_ENABLED=true}"
else
  SAAIA_BOOTSTRAP_API_KEY="${SAAIA_BOOTSTRAP_API_KEY:-}"
fi

LICENSE_SEATS="${SAAIA_LICENSE_SEATS:-1}"
if ! [[ "$LICENSE_SEATS" =~ ^[1-9][0-9]*$ ]]; then
  echo "SAAIA_LICENSE_SEATS must be a positive integer." >&2
  exit 2
fi

read_env_int() {
  local name="$1"
  local default="$2"
  local minimum="$3"
  local maximum="$4"
  local value="${!name:-$default}"

  if ! [[ "$value" =~ ^[0-9]+$ ]] \
    || (( value < minimum || value > maximum )); then
    echo "$name must be an integer between $minimum and $maximum." >&2
    exit 2
  fi

  printf '%s' "$value"
}

RERANK_ENABLED="true"
if [[ "${SAAIA_RERANK_ENABLED:-true}" =~ ^(0|false|no|off)$ ]]; then
  RERANK_ENABLED="false"
fi
RERANK_MAX_CANDIDATES="$(read_env_int SAAIA_RERANK_MAX_CANDIDATES 12 2 128)"

RAG_SEARCH_MAX_CONCURRENCY="$(read_env_int SAAIA_RAG_SEARCH_MAX_CONCURRENCY 4 1 64)"
RAG_SEARCH_QUEUE_LIMIT="$(read_env_int SAAIA_RAG_SEARCH_QUEUE_LIMIT 16 0 4096)"
RAG_SEARCH_QUEUE_WAIT_TIMEOUT_SECONDS="$(read_env_int SAAIA_RAG_SEARCH_QUEUE_WAIT_TIMEOUT_SECONDS 25 1 3600)"
RAG_SEARCH_RETRY_AFTER_SECONDS="$(read_env_int SAAIA_RAG_SEARCH_RETRY_AFTER_SECONDS 3 1 3600)"
INGESTION_CHUNK_MAX_WORDS="$(read_env_int SAAIA_INGESTION_CHUNK_MAX_WORDS 220 25 5000)"
INGESTION_CHUNK_OVERLAP_WORDS="$(read_env_int SAAIA_INGESTION_CHUNK_OVERLAP_WORDS 0 0 5000)"
INGESTION_CHUNK_MIN_WORDS="$(read_env_int SAAIA_INGESTION_CHUNK_MIN_WORDS 25 1 5000)"
INGESTION_EMBEDDINGS_BATCH_SIZE="$(read_env_int SAAIA_INGESTION_EMBEDDINGS_BATCH_SIZE 16 1 256)"
INGESTION_WORKER_CONCURRENCY="$(read_env_int SAAIA_INGESTION_WORKER_CONCURRENCY 2 1 32)"
INGESTION_TEI_MAX_CONCURRENCY="$(read_env_int SAAIA_INGESTION_TEI_MAX_CONCURRENCY 1 1 16)"
INGESTION_QDRANT_MAX_CONCURRENCY="$(read_env_int SAAIA_INGESTION_QDRANT_MAX_CONCURRENCY 4 1 32)"
INGESTION_TEI_INTERACTIVE_QUIET_PERIOD_MS="$(read_env_int SAAIA_INGESTION_TEI_INTERACTIVE_QUIET_PERIOD_MS 1500 0 600000)"
INGESTION_HEAVY_COMPUTE_MAX_CONCURRENCY="$(read_env_int SAAIA_INGESTION_HEAVY_COMPUTE_MAX_CONCURRENCY 1 1 16)"
INGESTION_BULKHEAD_ACQUIRE_TIMEOUT_SECONDS="$(read_env_int SAAIA_INGESTION_BULKHEAD_ACQUIRE_TIMEOUT_SECONDS 30 1 86400)"
INGESTION_OCR_BULKHEAD_ACQUIRE_TIMEOUT_SECONDS="$(read_env_int SAAIA_INGESTION_OCR_BULKHEAD_ACQUIRE_TIMEOUT_SECONDS 1800 1 86400)"
INGESTION_OCR_BULKHEAD_QUEUE_WAIT_TIMEOUT_SECONDS="$(read_env_int SAAIA_OCR_BULKHEAD_QUEUE_WAIT_TIMEOUT_SECONDS 21600 1 86400)"
INGESTION_HEAVY_COMPUTE_QUEUE_WAIT_TIMEOUT_SECONDS="$(read_env_int SAAIA_INGESTION_HEAVY_COMPUTE_QUEUE_WAIT_TIMEOUT_SECONDS 21600 1 86400)"

REQ_QDRANT=false
if [[ -n "${REQUIRE_QDRANT_AUTH_IN_PROD:-}" ]]; then
  if [[ "${REQUIRE_QDRANT_AUTH_IN_PROD,,}" =~ ^(1|true|yes|on)$ ]]; then
    REQ_QDRANT=true
  fi
fi

if $REQ_QDRANT && [[ -z "${QDRANT_API_KEY:-}" ]]; then
  echo "REQUIRE_QDRANT_AUTH_IN_PROD=true but QDRANT_API_KEY is empty. Set QDRANT_API_KEY in infra/.env." >&2
  exit 2
fi

# ---------------------------
# Resolve InstallRoot (bind-mount base)
# Priority:
#   1) SAAIA_INSTALL_ROOT
#   2) legacy: SAAIA_DEPLOY_DIR when absolute and NOT ending with /deploy
#   3) repo root
# ---------------------------
INSTALL_ROOT="${SAAIA_INSTALL_ROOT:-}"
if [[ -z "$INSTALL_ROOT" && -n "${SAAIA_DEPLOY_DIR:-}" ]]; then
  if [[ "${SAAIA_DEPLOY_DIR}" = /* ]]; then
    base="$(basename "${SAAIA_DEPLOY_DIR%/}")"
    if [[ "${base,,}" != "deploy" ]]; then
      INSTALL_ROOT="$SAAIA_DEPLOY_DIR"
    else
      INSTALL_ROOT="$(dirname "$SAAIA_DEPLOY_DIR")"
    fi
  fi
fi
INSTALL_ROOT="${INSTALL_ROOT:-$ROOT}"

DEPLOY_EXPECTED="$INSTALL_ROOT/deploy"

# Optional extra deploy dir (compat with older docs)
EXTRA_DEPLOY=""
if [[ -n "${SAAIA_DEPLOY_DIR:-}" ]]; then
  if [[ "${SAAIA_DEPLOY_DIR}" = /* ]]; then
    base="$(basename "${SAAIA_DEPLOY_DIR%/}")"
    if [[ "${base,,}" = "deploy" ]]; then
      EXTRA_DEPLOY="$SAAIA_DEPLOY_DIR"
    else
      EXTRA_DEPLOY="$SAAIA_DEPLOY_DIR/deploy"
    fi
  else
    EXTRA_DEPLOY="$INSTALL_ROOT/$SAAIA_DEPLOY_DIR"
  fi
fi

mkdir -p "$DEPLOY_EXPECTED" "$INSTALL_ROOT/documents" "$INSTALL_ROOT/data/backend" "$INSTALL_ROOT/data/postgres" "$INSTALL_ROOT/data/qdrant" "$INSTALL_ROOT/cache/tei"

if ! $REQ_QDRANT; then
  # Back-compat: if operator didn't set REQUIRE_QDRANT_AUTH_IN_PROD explicitly,
  # infer it from QDRANT_API_KEY presence.
  if [[ -n "${QDRANT_API_KEY:-}" ]]; then REQ_QDRANT=true; fi
fi

CFG="$DEPLOY_EXPECTED/deployment.config.json"
SIG="$DEPLOY_EXPECTED/deployment.config.sig"

# Render template (minimal escaping; use PowerShell version on Windows for safer JSON escaping)
sed \
  -e "s/__AUTH_PEPPER__/${SAAIA_AUTH_PEPPER}/g" \
  -e "s/__BOOTSTRAP_API_KEY__/${SAAIA_BOOTSTRAP_API_KEY}/g" \
  -e "s/__BOOTSTRAP_ENABLED__/${BOOTSTRAP_ENABLED}/g" \
  -e "s/__POSTGRES_PASSWORD__/${POSTGRES_PASSWORD}/g" \
  -e "s/__POSTGRES_DB__/${POSTGRES_DB:-saaia}/g" \
  -e "s/__POSTGRES_USER__/${POSTGRES_USER:-saaia}/g" \
  -e "s#__TEI_MODEL_ID__#${TEI_MODEL_ID:-intfloat/multilingual-e5-base}#g" \
  -e "s/__TEI_MODEL_REVISION__/${TEI_MODEL_REVISION:-d128750597153bb5987e10b1c3493a34e5a4502a}/g" \
  -e "s/__RERANK_ENABLED__/${RERANK_ENABLED}/g" \
  -e "s#__RERANK_MODEL_ID__#${SAAIA_RERANK_MODEL_ID:-Alibaba-NLP/gte-multilingual-reranker-base}#g" \
  -e "s/__RERANK_MAX_CANDIDATES__/${RERANK_MAX_CANDIDATES}/g" \
  -e "s/__RAG_SEARCH_MAX_CONCURRENCY__/${RAG_SEARCH_MAX_CONCURRENCY}/g" \
  -e "s/__RAG_SEARCH_QUEUE_LIMIT__/${RAG_SEARCH_QUEUE_LIMIT}/g" \
  -e "s/__RAG_SEARCH_QUEUE_WAIT_TIMEOUT_SECONDS__/${RAG_SEARCH_QUEUE_WAIT_TIMEOUT_SECONDS}/g" \
  -e "s/__RAG_SEARCH_RETRY_AFTER_SECONDS__/${RAG_SEARCH_RETRY_AFTER_SECONDS}/g" \
  -e "s/__INGESTION_CHUNK_MAX_WORDS__/${INGESTION_CHUNK_MAX_WORDS}/g" \
  -e "s/__INGESTION_CHUNK_OVERLAP_WORDS__/${INGESTION_CHUNK_OVERLAP_WORDS}/g" \
  -e "s/__INGESTION_CHUNK_MIN_WORDS__/${INGESTION_CHUNK_MIN_WORDS}/g" \
  -e "s/__INGESTION_EMBEDDINGS_BATCH_SIZE__/${INGESTION_EMBEDDINGS_BATCH_SIZE}/g" \
  -e "s/__INGESTION_WORKER_CONCURRENCY__/${INGESTION_WORKER_CONCURRENCY}/g" \
  -e "s/__INGESTION_TEI_MAX_CONCURRENCY__/${INGESTION_TEI_MAX_CONCURRENCY}/g" \
  -e "s/__INGESTION_QDRANT_MAX_CONCURRENCY__/${INGESTION_QDRANT_MAX_CONCURRENCY}/g" \
  -e "s/__INGESTION_TEI_INTERACTIVE_QUIET_PERIOD_MS__/${INGESTION_TEI_INTERACTIVE_QUIET_PERIOD_MS}/g" \
  -e "s/__INGESTION_HEAVY_COMPUTE_MAX_CONCURRENCY__/${INGESTION_HEAVY_COMPUTE_MAX_CONCURRENCY}/g" \
  -e "s/__INGESTION_BULKHEAD_ACQUIRE_TIMEOUT_SECONDS__/${INGESTION_BULKHEAD_ACQUIRE_TIMEOUT_SECONDS}/g" \
  -e "s/__INGESTION_OCR_BULKHEAD_ACQUIRE_TIMEOUT_SECONDS__/${INGESTION_OCR_BULKHEAD_ACQUIRE_TIMEOUT_SECONDS}/g" \
  -e "s/__INGESTION_OCR_BULKHEAD_QUEUE_WAIT_TIMEOUT_SECONDS__/${INGESTION_OCR_BULKHEAD_QUEUE_WAIT_TIMEOUT_SECONDS}/g" \
  -e "s/__INGESTION_HEAVY_COMPUTE_QUEUE_WAIT_TIMEOUT_SECONDS__/${INGESTION_HEAVY_COMPUTE_QUEUE_WAIT_TIMEOUT_SECONDS}/g" \
  -e "s/__REQUIRE_QDRANT_AUTH__/${REQ_QDRANT}/g" \
  -e "s/__LICENSE_SEATS__/${LICENSE_SEATS}/g" \
  "$TEMPLATE" > "$CFG"

if grep -Eq '__[A-Z0-9_]+__' "$CFG"; then
  echo "Rendered deployment config still contains unresolved placeholders:" >&2
  grep -Eo '__[A-Z0-9_]+__' "$CFG" | sort -u >&2
  exit 2
fi

# Sign
dotnet run --project "$ROOT/tools/ConfigSigner/ConfigSigner.csproj" -- sign "$CFG" "@${SAAIA_CONFIG_PRIVATE_KEY_PATH}" "$SIG"

# Duplicate config if user requested an alternate deploy dir
if [[ -n "$EXTRA_DEPLOY" && "$EXTRA_DEPLOY" != "$DEPLOY_EXPECTED" ]]; then
  mkdir -p "$EXTRA_DEPLOY"
  cp -f "$CFG" "$EXTRA_DEPLOY/deployment.config.json"
  cp -f "$SIG" "$EXTRA_DEPLOY/deployment.config.sig"
  echo "WARNING: SAAIA_DEPLOY_DIR resolves to '$EXTRA_DEPLOY' but compose mounts '$DEPLOY_EXPECTED'. Wrote config to BOTH."
fi

export SAAIA_INSTALL_ROOT="$INSTALL_ROOT"

docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" up -d --build

echo "== Verify /ready =="
PORT="${BACKEND_HOST_PORT:-5122}"
READY_URL="http://localhost:${PORT}/ready"
for i in {1..40}; do
  if curl -fsS "$READY_URL" >/dev/null 2>&1; then
    echo "/ready => 200 (try $i/40)"
    exit 0
  fi
  sleep 1.5
done

echo "WARNING: Could not reach backend /ready. Check logs: docker compose -f infra/docker-compose.prod.yml --env-file infra/.env logs -f backend" >&2
