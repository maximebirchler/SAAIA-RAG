#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
MODELS_DIR="$ROOT_DIR/models"
ENV_FILE="$ROOT_DIR/infra/.env"
ENV_EXAMPLE="$ROOT_DIR/infra/.env.example"

mkdir -p "$MODELS_DIR"

read_env_value() {
  local key="$1"
  local file="$2"
  [[ -f "$file" ]] || return 0
  local line
  line="$(grep -E "^${key}=" "$file" | tail -n 1 || true)"
  [[ -z "$line" ]] && return 0
  echo "${line#*=}"
}

file_size() {
  local f="$1"
  if stat --version >/dev/null 2>&1; then stat -c%s "$f"; else stat -f%z "$f"; fi
}

sha256_file() {
  local f="$1"
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$f" | awk '{print toupper($1)}'
  else
    shasum -a 256 "$f" | awk '{print toupper($1)}'
  fi
}

MODEL_URL="${1:-}"
MODEL_FILE="${2:-}"

MODEL_URL="${MODEL_URL:-${LLM_MODEL_URL:-}}"
MODEL_FILE="${MODEL_FILE:-${LLM_MODEL_FILE:-${LLM_MODEL:-}}}"

if [[ -z "$MODEL_URL" || -z "$MODEL_FILE" ]]; then
  MODEL_URL="${MODEL_URL:-$(read_env_value "LLM_MODEL_URL" "$ENV_FILE")}"
  MODEL_FILE="${MODEL_FILE:-$(read_env_value "LLM_MODEL_FILE" "$ENV_FILE")}"
  MODEL_FILE="${MODEL_FILE:-$(read_env_value "LLM_MODEL" "$ENV_FILE")}"
fi

if [[ -z "$MODEL_URL" || -z "$MODEL_FILE" ]]; then
  MODEL_URL="${MODEL_URL:-$(read_env_value "LLM_MODEL_URL" "$ENV_EXAMPLE")}"
  MODEL_FILE="${MODEL_FILE:-$(read_env_value "LLM_MODEL_FILE" "$ENV_EXAMPLE")}"
  MODEL_FILE="${MODEL_FILE:-$(read_env_value "LLM_MODEL" "$ENV_EXAMPLE")}"
fi

if [[ -z "${MODEL_URL}" || -z "${MODEL_FILE}" ]]; then
  echo "ERROR: Missing MODEL_URL / MODEL_FILE."
  echo "Fix by:"
  echo "  - Copy infra/.env.example -> infra/.env"
  echo "  - Set LLM_MODEL_URL + LLM_MODEL_FILE"
  echo "Or run:"
  echo "  ./infra/scripts/download-model.sh <MODEL_URL> <MODEL_FILE>"
  exit 1
fi

EXPECTED_SIZE="${LLM_MODEL_SIZE_BYTES:-$(read_env_value "LLM_MODEL_SIZE_BYTES" "$ENV_FILE")}"
EXPECTED_SHA="${LLM_MODEL_SHA256:-$(read_env_value "LLM_MODEL_SHA256" "$ENV_FILE")}"
EXPECTED_SIZE="${EXPECTED_SIZE:-$(read_env_value "LLM_MODEL_SIZE_BYTES" "$ENV_EXAMPLE")}"
EXPECTED_SHA="${EXPECTED_SHA:-$(read_env_value "LLM_MODEL_SHA256" "$ENV_EXAMPLE")}"

OUT_PATH="$MODELS_DIR/$MODEL_FILE"

echo "Downloading model..."
echo "  URL : $MODEL_URL"
echo "  OUT : $OUT_PATH"

if [[ -f "$OUT_PATH" ]]; then
  echo "Already exists: $OUT_PATH"
  ls -lh "$OUT_PATH"
  exit 0
fi

if command -v curl >/dev/null 2>&1; then
  curl -L --fail --retry 5 --retry-delay 2 -o "$OUT_PATH.part" "$MODEL_URL"
  mv "$OUT_PATH.part" "$OUT_PATH"
elif command -v wget >/dev/null 2>&1; then
  wget -O "$OUT_PATH.part" "$MODEL_URL"
  mv "$OUT_PATH.part" "$OUT_PATH"
else
  echo "ERROR: curl or wget is required."
  exit 2
fi

# Verify size (optional)
if [[ -n "${EXPECTED_SIZE:-}" ]]; then
  ACTUAL_SIZE="$(file_size "$OUT_PATH")"
  if [[ "$ACTUAL_SIZE" != "$EXPECTED_SIZE" ]]; then
    echo "ERROR: size mismatch. expected=$EXPECTED_SIZE actual=$ACTUAL_SIZE"
    exit 3
  fi
  echo "OK size: $ACTUAL_SIZE bytes"
fi

# Verify sha256 (optional)
if [[ -n "${EXPECTED_SHA:-}" ]]; then
  ACTUAL_SHA="$(sha256_file "$OUT_PATH")"
  EXPECTED_SHA_UP="$(echo "$EXPECTED_SHA" | tr '[:lower:]' '[:upper:]')"
  if [[ "$ACTUAL_SHA" != "$EXPECTED_SHA_UP" ]]; then
    echo "ERROR: sha256 mismatch."
    echo "expected=$EXPECTED_SHA_UP"
    echo "actual  =$ACTUAL_SHA"
    exit 4
  fi
  echo "OK sha256: $ACTUAL_SHA"
fi

echo "Done."
ls -lh "$OUT_PATH"
