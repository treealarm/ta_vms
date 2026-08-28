# Текущий статус (handoff)

Дата среза: 2026-08-28.

Коротко: библиотечный трек v3 в части ROI/temporal/rate-control существенно продвинут;
последние изменения закрывали наблюдаемость fallback для CPU H.265 и observability по
ROI lag (§14). Полный `integration/scripts/check.sh` на этом состоянии проходит.

## Что уже сделано (по коду)

- **Slice A (§11.1):** публичная лестница приоритетов `ObjectPriority` внедрена.
- **Slice B (§11.2–11.3):** геометрические дефолты v3 (`min_roi_side`, `margin_px`) внедрены.
- **Slice C (§13):** temporal anti-flicker (smoothing/slew/prediction) реализован на library path.
- **Slice D (§15–16):**
  - CPU H.264: `target_bitrate` работает через CBR+filler.
  - Matched-bitrate проверяется скриптом `matched-bitrate.sh` (две acceptance-gates).
  - CPU H.265: native bitrate path отключен (fallback на CRF/CQP) из-за стабильности.
- **Slice E (§14):** ROI lag хелперы + runtime observability в `Report` и логах
  для `transcode_file` и `Session` path.
- **Service telemetry:** ROI lag и CPU H.265 guard прокинуты в gRPC/job ответы
  (`Job`, stream `Done`) и в статус-событие `roi-transcode-job-changed`.
- **SEI / stability:** для CPU H.265 отключен `sei` capability (практический guard от крэшей ROI+SEI).

## Последние шаги (что именно добавили недавно)

- Явный guard-лог для CPU H.265 при `target_bitrate > 0`:
  - `"guard: cpu h265 target-bitrate disabled; using CRF/CQP"`.
- Флаг в `Report`: `cpu_h265_target_guard` (fallback виден через API, не только в stderr).
- Метрики ROI lag в `Report`:
  - `roi_lag_measured`, `roi_lag_samples`, `roi_lag_min_iou`,
  - `roi_lag_p95_frames`, `roi_lag_p99_frames`,
  - `roi_lag_p95_ms`, `roi_lag_p99_ms`, `roi_lag_acceptance`.
- Интеграционные проверки в `check.sh`:
  - guard CPU H.265 виден в логах;
  - ROI lag метрики наблюдаемы на tracked ROI и проходят acceptance.

## На какой мы стадии сейчас

- По порядку из `plan-v2.md`: практически закрыты этапы 1–4 на текущем уровне реализации.
- Основные незакрытые направления:
  - NVENC полноценный ROI path.
  - Rockchip MPP backend.
  - HW-native bitrate path с сохранением ROI semantics.
  - Датасеты/quality stack уровня приёмки ТЗ (VMAF/task-aware/regression framework).

## Что брать следующим агентом (рекомендуемый порядок)

1. **Докрутить критерии acceptance на реальных клипах:** вынести ROI lag smoke не только
   на synthetic `moving`, но и на один реальный короткий клип с track_id.
2. **Стабилизация H.265 roadmap:** либо доказуемо вернуть SEI/native bitrate на CPU H.265,
   либо зафиксировать guard как долгосрочный контракт в документации и API.
3. **Real-clip ROI lag regression:** добавить стабильный short fixture с track_id для
   проверки lag acceptance ближе к production-потоку.

## Быстрые команды проверки

```bash
# из roi_transcode/
BUILD=build/integration LIBBUILD=build ./integration/scripts/check.sh
./integration/scripts/matched-bitrate.sh <video> <roi.json> <out_dir> [target_bps] [target_tol_frac]
```

