#!/usr/bin/env bash
# Points a file camera's stream at another clip (or folder) and tells the services to reload.
#
# The gateway's API needs a login, and this is a dev tool, so it writes ta_cfg directly and then
# sends the event VmsCfg would have sent -- see docs/test-camera-from-file.md. VmsFs caches camera
# setup with no TTL, so without the event the change is simply not seen.
#
#   scripts/set-file-camera-clip.sh <camera-id> <path> [stream-name]
set -euo pipefail

CAMERA="${1:?camera id required}"
CLIP="${2:?clip path required}"
STREAM="${3:-}"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

if [[ ! -e "$CLIP" ]]; then
  echo "no such file or directory: $CLIP" >&2
  exit 1
fi

psql() { docker compose exec -T postgres psql -U postgres -d ta_cfg "$@"; }

if [[ -z "$STREAM" ]]; then
  STREAM=$(psql -At -c \
    "SELECT stream_name FROM camera_stream WHERE camera_id = '$CAMERA' ORDER BY stream_name LIMIT 1;")
  [[ -n "$STREAM" ]] || { echo "camera '$CAMERA' has no streams" >&2; exit 1; }
fi

psql -c "UPDATE camera_stream SET path = '$CLIP' WHERE camera_id = '$CAMERA' AND stream_name = '$STREAM';" >/dev/null
SERVER=$(psql -At -c "SELECT server_id FROM camera_setup WHERE camera_id = '$CAMERA';")

# Every subscriber refetches for itself; the payload only says which entries to drop.
curl -fsS -X POST "localhost:${DAPR_HTTP_PORT:-3600}/v1.0/publish/orderpubsub/server-config-changed" \
  -H 'Content-Type: application/json' \
  -d "{\"server_id\":\"${SERVER}\",\"camera_ids\":[\"${CAMERA}\"]}" >/dev/null

echo "camera ${CAMERA} stream '${STREAM}' -> ${CLIP}"
