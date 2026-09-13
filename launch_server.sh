#!/bin/bash
# Launches the fcadserve service for local testing.
# Usage: ./launch_server.sh [port]
set -euo pipefail

cd "$(dirname "$0")"

PORT="${1:-8777}"
ROOT="$(pwd)"
DLL="$ROOT/src/fcadserve/bin/Debug/net10.0/fcadserve.dll"

echo "building service (incremental)..." >&2
dotnet build src/fcadserve/fcadserve.csproj --nologo -v q >&2

if [ -n "${FCADSERVE_NO_CLEAN:-}" ]; then
    echo "skipping cleanup of work dirs (FCADSERVE_NO_CLEAN set)" >&2
else
    echo "cleaning work/e2e_state and work/e2e_out (set FCADSERVE_NO_CLEAN=1 to keep them)" >&2
    rm -rf "$ROOT/work/e2e_state" "$ROOT/work/e2e_out"
fi
mkdir -p "$ROOT/work/e2e_out"

cat <<EOF >&2
launching fcadserve service on http://127.0.0.1:$PORT (Ctrl-C to stop)

try it:
  curl -s http://127.0.0.1:$PORT/readyz
  # stage an STL, then submit the returned stl_path:
  curl -s -X POST 'http://127.0.0.1:$PORT/v1/uploads?name=box.stl' \\
       --data-binary @$ROOT/work/box.stl
  curl -s -X POST http://127.0.0.1:$PORT/v1/jobs \\
       -H 'content-type: application/json' \\
       -d '{"stl_path":"'$ROOT'/work/box.stl","output_dir":"'$ROOT'/work/e2e_out"}'
EOF

exec env \
    FCADSERVE_STATE_ROOT="$ROOT/work/e2e_state" \
    FCADSERVE_ALLOWED_INPUT_ROOTS="$ROOT/work" \
    FCADSERVE_ALLOWED_OUTPUT_ROOTS="$ROOT/work/e2e_out" \
    FCADSERVE_WORKER_SCRIPT="$ROOT/freecad/fcadserve_worker.py" \
    FCADSERVE_RENDER_SCRIPT="$ROOT/freecad/fcadserve_render.py" \
    FCADSERVE_FREECAD__MODE=flatpak \
    FCADSERVE_JOBS__TIMEOUT_SECONDS=300 \
    FCADSERVE_PORT="$PORT" \
    dotnet "$DLL"