# Matched-bitrate acceptance (TZ v3 §15–16)

v3 asks that adaptive and baseline encodes land within **2 %** of the same
average bitrate, then compares ROI quality. Our encoder still runs CQP/CRF;
`BitrateBudget` is an outer loop, not native VBR/VBV.

## Reference profile (as close as we can get today)

| Knob | Value | Notes |
|------|-------|-------|
| GOP | 60 | `Settings::gop` |
| B-frames | 0 | backend default / disable where exposed |
| Rate control | CQP/CRF + optional `--target-bitrate` | not encoder VBR |
| AQ / lookahead | off where the backend allows | CPU may still differ |

## How to measure

```bash
# From roi_transcode/ after building tools:
./integration/scripts/matched-bitrate.sh \
  /path/to/video6.mp4 \
  /path/to/boxes.json \
  /tmp/matched-out \
  500000
```

The script encodes baseline (no ROI / flat quality) and adaptive (ROI on), both
with the same target average when given, then prints size and estimated bitrate
and the relative difference. Exit 0 if `|adaptive - baseline| / baseline ≤ 2%`
when both targets were applied; otherwise non-zero with the numbers printed.

If the product CRF already sits under the target, the outer loop is a no-op —
match is then accidental, not proof of VBR. Prefer a target **below** the
unconstrained CRF size for a real stress.

## Path to native VBR/VBV

1. Keep the ObjectPriority ladder + geometry as the policy under test (slices A–B).
2. Per-backend VBR open (x264 `bitrate`+VBV, QSV/VA-API rate control enums).
3. Object-first spend inside that mode (or keep outer loop only as fallback).
4. Re-run this script; retire “CRF36 file got smaller” as the primary story.
