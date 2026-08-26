# Library work toward TZ v3 (implementation notes)

Companion to the Cursor plan «План v3 библиотеки». Does **not** replace
[`plan-v2.md`](plan-v2.md): baton for that file is with Danil (see
[`COLLABORATION.md`](COLLABORATION.md)). This file records what landed in code
on the library side so the two tracks stay visible.

**Out of early scope (unchanged):** ta_vms archive/Dapr, NVENC, Rockchip, VMAF.

## Slice A — ObjectPriority ladder (§11.1)

- Public type: [`object_priority.h`](../include/smartvideo/object_priority.h)
  (`Critical/−8` … `Background/0`).
- Continuous v2 importance remains the **internal** way to pick a step
  (`priority_from_importance`); `Quality::use_v3_priority_ladder` (default
  **true**) selects ladder QP vs the old line.
- `Box::desired_qp_delta` overrides the ladder when set.
- Unit: `v3_policy_test`, updated expectations in `importance_test`.

## Slice B — Geometry (§11.2–11.3)

- Defaults: `min_roi_side = 64`, `margin_mode = FixedPixels`, `margin_px = 8`.
- `FractionOfFrame` keeps historical `pad` as halo-only (no body inflate).
- Body margin uses `margin_px` only; `KindQuality.pad` / `Quality.pad` stay the
  soft rim — see [`padding-sweep.md`](padding-sweep.md) (halo **width** matters
  more than QP boost; do not treat `pad=0.02` as v3 truth).

## Slice C — Temporal QP slew (§13)

- `TemporalROIConfig::max_qp_delta_per_frame` is implemented (`QpSlewLimiter` in
  session + file paths). Default remains **0** (off).
- **Decision (2026-08-26, Cursor library track):** our Temporal defaults stay
  zero for smoothing / prediction / `max_qp_delta_per_frame`. We **diverge** from
  the PDF’s non-zero §13 defaults on purpose: open still refuses
  spatial/temporal smoothing and track prediction. Enabling PDF defaults would
  reject every job until those features exist. Documented here and as a dated
  note in `plan-v2.md`; not a silent code change of the refused fields.

## Slice D — Matched bitrate (§15–16)

- Acceptance helper: [`matched-bitrate.md`](matched-bitrate.md) +
  `integration/scripts/matched-bitrate.sh`.
- Native object-first VBR/VBV inside backends is **not** done; outer
  `BitrateBudget` remains the bridge. Path forward listed in that doc.

## Slice E — ROI lag (§14)

- Helpers: `src/adaptive/roi_lag.{h,cpp}` — IoU, lag_frames/ms, P95/P99
  acceptance (`IoU≥0.50`, P95≤3 frames/100 ms, P99≤6/200 ms).
- Covered by `v3_policy_test`. Wiring into a live quality report is later.

## Proto

v3-first wire (breaking, intentional): `ObjectPriority` + `desired_qp_delta` on `Box`;
`margin_mode` / `margin_px` / `min_roi_side` on `RoiQuality`; `ClassWeighting` (tables
only, no importance→QP line); `TemporalRoiConfig` with implemented fields only.
Vendored into `video_a/proto/roi_transcode.proto`. ta_vms archive callers adjust later.
