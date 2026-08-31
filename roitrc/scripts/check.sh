#!/usr/bin/env bash
# What roitrc has to get right, with one exit code and nothing for a human to look at.
#
# This is the half of the encoder's check.sh that tested the *service* rather than the
# library: startup refusing a broken configuration, both gRPC entry points, and the path
# sandbox. It moved here on 2026-08-31 with the service itself. The library's own
# acceptance -- ROI quality, backends, frame/file parity -- stays in its repository, where
# it runs against the library directly and never needed gRPC to do it.
#
#   ./scripts/check.sh
#   BUILD=build ./scripts/check.sh
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD="${BUILD:-${ROOT}/build}"
SVC="${BUILD}/roi-transcode-svc"
SMOKE="${BUILD}/roi_grpc_smoke"
# Boxes are normalized 0..1, so the fixture from the library's repository fits whatever
# clip we generate below.
ROI="${ROOT}/sve/testdata/synth_roi.json"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fails=0
step() { printf '\n== %s ==\n' "$1"; }
pass() { printf '  ok    %s\n' "$1"; }
fail() { printf '  FAIL  %s\n' "$1"; fails=$((fails + 1)); }
info() { printf '  info  %s\n' "$1"; }

for f in "$SVC" "$SMOKE"; do
    [[ -x "$f" ]] || { echo "not built: $f (cmake --build $BUILD)" >&2; exit 1; }
done
[[ -f "$ROI" ]] || { echo "missing $ROI -- git submodule update --init" >&2; exit 1; }

# A clip of our own rather than the library's synthetic fixture: that one is generated, not
# committed, and this suite tests plumbing, for which any decodable video will do.
SRC="${WORK}/src.mp4"
ffmpeg -hide_banner -loglevel error -f lavfi -i testsrc2=size=640x360:rate=25:duration=4 \
    -c:v libx264 -pix_fmt yuv420p -g 25 "$SRC" </dev/null || { echo "ffmpeg failed" >&2; exit 1; }
SRC_FRAMES=$(ffprobe -v error -select_streams v:0 -count_packets \
    -show_entries stream=nb_read_packets -of csv=p=0 "$SRC")

step "startup refuses a broken configuration"
# Every required variable is checked before the socket opens, so a misconfigured sandbox
# kills the process instead of the first request that walks out of it.
for spec in APP_ID ROI_ALLOWED_ROOTS relative-root; do
    case "$spec" in
      APP_ID)            out=$(env -u APP_ID -u GRPC_ROI_BIND -u GRPC_ROI_PORT \
                               -u ROI_ALLOWED_ROOTS "$SVC" 2>&1); rc=$? ;;
      ROI_ALLOWED_ROOTS) out=$(env -u ROI_ALLOWED_ROOTS APP_ID=t GRPC_ROI_BIND=127.0.0.1 \
                               GRPC_ROI_PORT=50099 "$SVC" 2>&1); rc=$? ;;
      relative-root)     out=$(env APP_ID=t GRPC_ROI_BIND=127.0.0.1 GRPC_ROI_PORT=50099 \
                               ROI_ALLOWED_ROOTS=not/absolute "$SVC" 2>&1); rc=$? ;;
    esac
    if [[ $rc -ne 0 && -n "$out" ]]; then pass "refuses ${spec}"; else fail "refuses ${spec}"; fi
done

step "gRPC: both entry points, and the sandbox"
port=$(python3 -c 'import socket;s=socket.socket();s.bind(("127.0.0.1",0));print(s.getsockname()[1]);s.close()')
APP_ID=check GRPC_ROI_BIND=127.0.0.1 GRPC_ROI_PORT="$port" \
    ROI_ALLOWED_ROOTS="$WORK" "$SVC" >"${WORK}/svc.log" 2>&1 &
svc=$!
for _ in $(seq 40); do
    grep -q 'listening on' "${WORK}/svc.log" 2>/dev/null && break
    sleep 0.25
done

if "$SMOKE" "127.0.0.1:${port}" "$SRC" "${WORK}/job.mp4" "$ROI" >"${WORK}/job.log" 2>&1; then
    pass "file job completed"
    # Which backend opened is the library's business and is asserted in its own repository.
    # Here it only has to be reported: a job that says nothing about how it encoded is a
    # job nobody can diagnose, and §50 forbids a fallback that leaves no trace.
    backend=$(grep -oE 'encoder=[^ ]+' "${WORK}/job.log" | head -1 | cut -d= -f2-)
    if [[ -n "$backend" ]]; then pass "job reports the backend it used"; info "backend: ${backend}"
    else fail "job reports no backend"; fi
    if grep -Eq "roi_lag\.measured=" "${WORK}/job.log"; then
        pass "file job exposes roi lag telemetry"
    else
        fail "file job roi lag telemetry missing"
    fi
else
    fail "file job over gRPC"; sed 's/^/      /' "${WORK}/job.log" >&2
fi

if "$SMOKE" "127.0.0.1:${port}" "$SRC" "" --stream >"${WORK}/stream.log" 2>&1; then
    streamed=$(grep -oE '[0-9]+ packets streamed back' "${WORK}/stream.log" | grep -oE '^[0-9]+')
    if [[ "${streamed:-0}" == "$SRC_FRAMES" ]]; then
        pass "stream returns a packet per frame (${streamed})"
    else
        fail "stream returned ${streamed:-0} packets for ${SRC_FRAMES} frames"
    fi
    if grep -Eq "roi_lag\.measured=true roi_lag\.samples=[1-9][0-9]*" "${WORK}/stream.log"; then
        pass "stream done exposes roi lag telemetry"
    else
        fail "stream done roi lag telemetry missing"
    fi
else
    fail "frame stream over gRPC"; sed 's/^/      /' "${WORK}/stream.log" >&2
fi

# A path outside ROI_ALLOWED_ROOTS must be refused, not written. The caller and the service
# share a filesystem, so this is checked on the service's side or not at all.
if "$SMOKE" "127.0.0.1:${port}" "$SRC" "/etc/roi_escape.mp4" "$ROI" \
    >"${WORK}/escape.log" 2>&1; then
    fail "a path outside the sandbox was accepted"
else
    pass "a path outside the sandbox is refused"
fi

kill "$svc" 2>/dev/null
wait "$svc" 2>/dev/null || true

step "unit tests"
if ctest --test-dir "$BUILD" --output-on-failure >"${WORK}/ctest.log" 2>&1; then
    pass "ctest"
else
    fail "ctest"; sed 's/^/      /' "${WORK}/ctest.log" >&2
fi

echo
if (( fails > 0 )); then
    echo "${fails} check(s) failed"
    exit 1
fi
echo "all checks passed"
