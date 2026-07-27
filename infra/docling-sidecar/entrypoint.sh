#!/usr/bin/env bash
set -euo pipefail

upstream_port="${SAAIA_DOCLING_UPSTREAM_PORT:-5002}"
listen_port="${SAAIA_DOCLING_LISTEN_PORT:-5001}"

python -m saaia_docling.upstream run \
  --host 127.0.0.1 \
  --port "${upstream_port}" \
  --workers 1 &
upstream_pid=$!

uvicorn saaia_docling.app:app \
  --host 0.0.0.0 \
  --port "${listen_port}" \
  --workers 1 \
  --no-access-log &
wrapper_pid=$!

shutdown() {
  kill -TERM "${wrapper_pid}" "${upstream_pid}" 2>/dev/null || true
  wait "${wrapper_pid}" "${upstream_pid}" 2>/dev/null || true
}

trap shutdown EXIT INT TERM

set +e
wait -n "${upstream_pid}" "${wrapper_pid}"
status=$?
set -e
exit "${status}"
