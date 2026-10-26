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
IDENTITY_HELPER="$ROOT/infra/scripts/prod/deployment_identity.py"
command -v python3 >/dev/null || { echo "Python 3 is required for offline deployment identity." >&2; exit 2; }
SAAIA_CODE_REVISION="$(python3 "$IDENTITY_HELPER" --repo-root "$ROOT" source backend)"
SAAIA_DOCLING_SOURCE_REVISION="$(python3 "$IDENTITY_HELPER" --repo-root "$ROOT" source docling)"
export SAAIA_CODE_REVISION SAAIA_DOCLING_SOURCE_REVISION

docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" pull || true
docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" up -d --build
docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" ps
