#!/bin/bash
# End-to-end integration test for fcadserve with the real FreeCAD stack.
#
# Requires on the host: dotnet 10, Xvfb, python3, and the FreeCAD flatpak
# (org.freecad.FreeCAD). If a requirement is missing the script prints
# SKIP and exits 0 so CI runs stay green on machines without FreeCAD.
#
# Verifies:
#   * /healthz and /readyz report ready
#   * snake_case submit -> 202, job runs to a terminal state
#   * report carries input.sha256 and created_at_utc
#   * step + render artifacts are published and downloadable
#   * resubmit with the same output directory is rejected with 409
#   * cancel terminates a job
#   * POST /v1/uploads stages an STL into the state root with matching sha256
set -euo pipefail

cd "$(dirname "$0")/.."
ROOT="$(pwd)"
PORT="${FCADSERVE_INTEGRATION_PORT:-17901}"
BASE="$ROOT/work/integration"

log() { printf 'integration: %s\n' "$*" >&2; }

require() {
    if ! command -v "$1" >/dev/null 2>&1; then
        log "SKIP: '$1' not found on PATH"
        exit 0
    fi
}
require python3
require curl
require Xvfb
require flatpak

if ! flatpak info org.freecad.FreeCAD >/dev/null 2>&1; then
    log "SKIP: org.freecad.FreeCAD flatpak not installed"
    exit 0
fi

DLL="$ROOT/src/fcadserve/bin/Release/net10.0/fcadserve.dll"
if [ ! -f "$DLL" ]; then
    log "building release binary..."
    dotnet publish src/fcadserve/fcadserve.csproj -c Release --nologo -v q >/dev/null
fi

# ---- fixtures ---------------------------------------------------------------
rm -rf "$BASE"; mkdir -p "$BASE/in" "$BASE/out" "$BASE/state"

python3 - "$BASE/in/box.stl" <<'PY'
import struct, sys
# A triangulated unit box (12 triangles) written as binary STL.
path = sys.argv[1]
box = [
    (0,0,0, 1,0,0, 1,1,0), (0,0,0, 1,1,0, 0,1,0),
    (1,0,0, 1,0,1, 1,1,1), (1,0,0, 1,1,1, 1,1,0),
    (0,1,0, 1,1,0, 1,1,1), (0,1,0, 1,1,1, 0,1,1),
    (0,0,1, 1,0,1, 1,1,1), (0,0,1, 1,1,1, 0,1,1),
    (0,0,0, 1,0,0, 1,0,1), (0,0,0, 1,0,1, 0,0,1),
    (0,0,0, 0,1,0, 0,1,1), (0,0,0, 0,1,1, 0,0,1),
]
with open(path, 'wb') as f:
    f.write(b'fcadserve integration stl'.ljust(80, b'\0'))
    f.write(struct.pack('<I', len(box)))
    for (x1,y1,z1,x2,y2,z2,x3,y3,z3) in box:
        f.write(struct.pack('<3f', 0, 0, 0))
        for x,y,z in ((x1,y1,z1),(x2,y2,z2),(x3,y3,z3)):
            f.write(struct.pack('<3f', x*30.0, y*30.0, z*30.0))
        f.write(struct.pack('<H', 0))
PY
log "stl fixture ready: $BASE/in/box.stl"
STL_SHA=$(sha256sum "$BASE/in/box.stl" | cut -d' ' -f1)

# Precedence check: FCADSERVE_PORT env says 17901, but the CLI arg must win.
log "starting server (env port=$PORT, CLI port=$((PORT+1)))"
env \
    FCADSERVE_STATE_ROOT="$BASE/state" \
    FCADSERVE_ALLOWED_INPUT_ROOTS="$BASE/in" \
    FCADSERVE_ALLOWED_OUTPUT_ROOTS="$BASE/out" \
    FCADSERVE_WORKER_SCRIPT="$ROOT/freecad/fcadserve_worker.py" \
    FCADSERVE_RENDER_SCRIPT="$ROOT/freecad/fcadserve_render.py" \
    FCADSERVE_FREECAD__MODE=flatpak \
    FCADSERVE_FREECAD__FLATPAK_APP_ID=org.freecad.FreeCAD \
    FCADSERVE_JOBS__TIMEOUT_SECONDS=300 \
    FCADSERVE_PORT="$PORT" \
    dotnet "$DLL" --Port=$((PORT+1)) > "$BASE/server.log" 2>&1 &
SERVER_PID=$!
trap 'kill "$SERVER_PID" 2>/dev/null || true' EXIT

CLI_HOST="http://127.0.0.1:$((PORT+1))"
ENV_HOST="http://127.0.0.1:$PORT"

deadline=$((SECONDS + 90))
until curl -sf "$CLI_HOST/healthz" >/dev/null 2>&1; do
    if [ "$SECONDS" -ge "$deadline" ]; then
        log "FAIL: server did not become healthy on CLI port $((PORT+1)) (CLI beat env)"
        sed -n '1,40p' "$BASE/server.log" >&2 || true
        exit 1
    fi
    sleep 1
done
if curl -sf "$ENV_HOST/healthz" >/dev/null 2>&1; then
    log "FAIL: server answered on the env-configured port; CLI did not override env"
    exit 1
fi
log "precedence ok: CLI --Port=$((PORT+1)) overrides FCADSERVE_PORT"

# readiness
deadline=$((SECONDS + 120))
until curl -sf "$CLI_HOST/readyz" | python3 -c 'import json,sys; sys.exit(0 if json.load(sys.stdin)["ready"] else 1)' >/dev/null 2>&1; do
    if [ "$SECONDS" -ge "$deadline" ]; then
        log "FAIL: /readyz never became ready"
        curl -s "$CLI_HOST/readyz" || true
        sed -n '1,60p' "$BASE/server.log" >&2 || true
        exit 1
    fi
    sleep 1
done
log "readyz ready"

# ---- upload ------------------------------------------------------------------
code=$(curl -s -o "$BASE/upload.json" -w '%{http_code}' \
    -X POST "$CLI_HOST/v1/uploads?name=box.stl" --data-binary @"$BASE/in/box.stl")
[ "$code" = "201" ] || { log "FAIL: upload returned $code (expected 201)"; cat "$BASE/upload.json" >&2 || true; exit 1; }
python3 - "$BASE/upload.json" "$STL_SHA" <<'PY'
import json, sys
up = json.load(open(sys.argv[1]))
assert up["sha256"] == sys.argv[2], up
assert up["name"] == "box.stl", up
assert up["stl_path"].startswith("/"), up
print("upload ok: sha256 matches fixture, path is absolute")
PY
log "upload staged with matching sha256"

# ---- submit (snake_case) ----------------------------------------------------
code=$(curl -s -o "$BASE/submit.json" -w '%{http_code}' -X POST "$CLI_HOST/v1/jobs" \
    -H 'Content-Type: application/json' \
    -d "{\"stl_path\":\"$BASE/in/box.stl\",\"output_dir\":\"$BASE/out\"}")
if [ "$code" != "202" ]; then
    log "FAIL: submit returned $code (expected 202)"
    cat "$BASE/submit.json" >&2 || true
    exit 1
fi
JOB_ID=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["job_id"])' "$BASE/submit.json")
log "submitted job $JOB_ID"

# ---- wait for terminal ------------------------------------------------------
deadline=$((SECONDS + 300))
STATUS=""
while [ "$SECONDS" -lt "$deadline" ]; do
    curl -sf "$CLI_HOST/v1/jobs/$JOB_ID" > "$BASE/job.json" || { sleep 2; continue; }
    STATUS=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["status"])' "$BASE/job.json")
    case "$STATUS" in
        succeeded|skipped|failed|cancelled) break ;;
    esac
    sleep 2
done
case "$STATUS" in
    succeeded|skipped) log "job reached terminal status: $STATUS" ;;
    *) log "FAIL: job did not finish (status=$STATUS)"; cat "$BASE/server.log" >&2 || true; exit 1 ;;
esac

# ---- report sha256 / created_at ---------------------------------------------
python3 - "$BASE/out/box.report.json" "$STL_SHA" <<'PY'
import json, sys
report = json.load(open(sys.argv[1]))
assert report["input"]["sha256"] == sys.argv[2], report["input"]
assert report["timestamps"]["created_at_utc"], report["timestamps"]
assert report["rendering"]["status"] == "done", report["rendering"]
assert report["validation"]["reimport_ok"] is True, report["validation"]
print("report ok: sha256 + created_at_utc + rendering done + reimport ok")
PY
log "report input sha256 matches and created_at_utc populated"

# ---- artifacts ---------------------------------------------------------------
art=$(curl -sf "$CLI_HOST/v1/jobs/$JOB_ID/artifacts")
for name in box.stp box_iso.png box_section.png box_left.png box_top.png box_right.png box_bottom.png box.report.json; do
    printf '%s' "$art" | python3 -c "
import json,sys
names=[a['name'] for a in json.load(sys.stdin).get('files',[])]
sys.exit(0 if '$name' in names else 1)" || { log "FAIL: artifact $name missing"; exit 1; }
done
donecode=$(curl -s -o "$BASE/box.stp" -w '%{http_code}' "$CLI_HOST/v1/jobs/$JOB_ID/artifacts/box.stp")
[ "$donecode" = "200" ] && [ -s "$BASE/box.stp" ] || { log "FAIL: artifact download failed ($donecode)"; exit 1; }
log "artifacts published and step downloadable"

# ---- collision ----------------------------------------------------------------
code=$(curl -s -o "$BASE/resubmit.json" -w '%{http_code}' -X POST "$CLI_HOST/v1/jobs" \
    -H 'Content-Type: application/json' \
    -d "{\"stl_path\":\"$BASE/in/box.stl\",\"output_dir\":\"$BASE/out\"}")
[ "$code" = "409" ] || { log "FAIL: resubmit returned $code (expected 409)"; exit 1; }
log "resubmit collision rejected with 409"

# ---- cancel -------------------------------------------------------------------
code=$(curl -s -o "$BASE/cancel_submit.json" -w '%{http_code}' -X POST "$CLI_HOST/v1/jobs" \
    -H 'Content-Type: application/json' \
    -d "{\"stl_path\":\"$BASE/in/box.stl\",\"output_dir\":\"$BASE/out/c2\"}")
[ "$code" = "202" ] || { log "FAIL: cancel-test submit returned $code"; exit 1; }
JOB2=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["job_id"])' "$BASE/cancel_submit.json")
curl -sf -X POST "$CLI_HOST/v1/jobs/$JOB2/cancel" -H 'Content-Type: application/json' > "$BASE/cancel.json"
python3 -c 'import json,sys; assert json.load(open(sys.argv[1]))["cancel_requested"] is True, sys.argv[1]' "$BASE/cancel.json"
deadline=$((SECONDS + 60))
STATUS2=""
while [ "$SECONDS" -lt "$deadline" ]; do
    curl -sf "$CLI_HOST/v1/jobs/$JOB2" > "$BASE/job2.json" || { sleep 1; continue; }
    STATUS2=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["status"])' "$BASE/job2.json")
    [ "$STATUS2" = "cancelled" ] && break
    sleep 1
done
[ "$STATUS2" = "cancelled" ] || { log "FAIL: cancelled job ended as '$STATUS2'"; exit 1; }
log "cancel terminated job $JOB2"

log "PASS: integration test completed"
trap - EXIT
kill "$SERVER_PID" 2>/dev/null || true
