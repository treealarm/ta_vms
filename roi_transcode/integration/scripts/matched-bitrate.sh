#!/usr/bin/env bash
# Matched-bitrate smoke for TZ v3 §16: adaptive vs baseline within 2%.
# Usage: matched-bitrate.sh <input.mp4> <boxes.json> <out_dir> [target_bps]
set -euo pipefail

if [[ $# -lt 3 ]]; then
  echo "usage: $0 <input.mp4> <boxes.json> <out_dir> [target_bps]" >&2
  exit 2
fi

INPUT=$1
BOXES=$2
OUT=$3
TARGET=${4:-0}

ROOT=$(cd "$(dirname "$0")/../.." && pwd)
CLIP="${ROOT}/integration/scripts/roi-clip.sh"
mkdir -p "$OUT"

COMMON=(--gop 60 --crf 28)
if [[ "$TARGET" != "0" ]]; then
  COMMON+=(--target-bitrate "$TARGET")
fi

echo "== baseline (no ROI kinds / empty protection path) =="
# Flat encode: still go through roi-clip but with empty samples if the JSON allows,
# otherwise encode adaptive with budget disabled via a sibling empty file.
BASE_JSON="$OUT/baseline_empty.json"
if [[ ! -f "$BASE_JSON" ]]; then
  printf '%s\n' '{"samples":[]}' >"$BASE_JSON"
fi

"$CLIP" "${COMMON[@]}" --input "$INPUT" --boxes "$BASE_JSON" --output "$OUT/baseline.mp4"
"$CLIP" "${COMMON[@]}" --input "$INPUT" --boxes "$BOXES" --output "$OUT/adaptive.mp4"

bytes() { wc -c <"$1" | tr -d ' '; }

B=$(bytes "$OUT/baseline.mp4")
A=$(bytes "$OUT/adaptive.mp4")

# Duration from ffprobe if present; else compare sizes only.
DUR=0
if command -v ffprobe >/dev/null 2>&1; then
  DUR=$(ffprobe -v error -show_entries format=duration -of default=nw=1:nk=1 "$OUT/baseline.mp4" || echo 0)
fi

python3 - "$B" "$A" "$DUR" "$TARGET" <<'PY'
import sys
b, a, dur, target = float(sys.argv[1]), float(sys.argv[2]), float(sys.argv[3]), float(sys.argv[4])
print(f"baseline_bytes={int(b)} adaptive_bytes={int(a)}")
if dur > 0:
    br_b = 8.0 * b / dur
    br_a = 8.0 * a / dur
    print(f"baseline_bps≈{br_b:.0f} adaptive_bps≈{br_a:.0f}")
    ref = br_b
    diff = abs(br_a - br_b) / ref if ref > 0 else 0.0
else:
    ref = b
    diff = abs(a - b) / ref if ref > 0 else 0.0
    print("duration unknown — comparing file sizes")
print(f"relative_diff={diff*100:.2f}% (limit 2.00%)")
if target > 0:
    print(f"requested_target_bps={int(target)}")
sys.exit(0 if diff <= 0.02 else 1)
PY
