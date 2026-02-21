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
  -e "s/__REQUIRE_QDRANT_AUTH__/${REQ_QDRANT}/g" \
  "$TEMPLATE" > "$CFG"

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
