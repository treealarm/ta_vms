# 23 — Убрать legacy-колонки охраны из VmsCfg (релиз D2)

**Найдено 2026-11-13** при переносе настроек логических объектов в VmsMap (ветка
`device-object-settings`, план `~/.claude/plans/sunny-beaming-origami.md`).

Нынешний релиз (D1) оставляет в VmsCfg колонки `camera_states.arm_mode` и
`device_settings.arm_mode/trigger_on/alarm_enabled` **только на чтение**: их однократно
переселяет в `ta_map.device_object_settings` `LegacyArmingImporter` при первом старте VmsMap, а
записи через RPC отвечают `FailedPrecondition`. Удалять их в том же релизе нельзя: установка,
обновляющаяся сразу с более старой версии, уже не отдаст значения, и режимы потеряются.

## Что сделать в D2 (после того как установки отработали на D1)

- VmsCfg, миграция `0003_drop_legacy_arming.sql`:
  `ALTER TABLE camera_states DROP COLUMN arm_mode;` и
  `ALTER TABLE device_settings DROP COLUMN arm_mode, DROP COLUMN trigger_on, DROP COLUMN alarm_enabled;`
  (таблица остаётся ради `name`).
- Удалить из `proto/vms_cfg.proto` (без `reserved`, как требует `CLAUDE.md`):
  `SetCamState`, `SetCamArmMode`, `SetDeviceArmMode`, поле `arm_mode` в `GetCamStateResponse` и
  `CameraArmState`, поля `arm_mode/trigger_on/alarm_enabled` в `DeviceSettings`.
  `GetCamState/GetCamStates` остаются ради `armed`; `ListDeviceSettings*` — ради имён.
- Удалить `CameraArmingRules` (остаётся только `ValidateCameraId` для `SetCamArmed` — перенести
  его в вызывающий код), `DeviceSettingsRules.Validate` сократить до имени; `DeviceStorage.SetAsync`
  оставить только с `name`.
- VmsMap: удалить `LegacyArmingImporter`, `ILegacyImportMarker`/`StateStoreImportMarker` и ключ
  `object-settings-seeded` из state store; убрать вызов из `StartupResyncService`.
- `EffectiveArmingResolver`/`CameraArmingReconciler`/`NodeArmingService`: убрать последние чтения
  `ICameraStateSource.ArmMode` (если останутся) — режим только из `device_object_settings`.
- Тесты: `VmsCfg.Tests/CameraStateTests` сократить до чтения `armed`; `DeviceSettingsTests` — до
  имени; `LegacyArmingImporterTests` удалить.
- Проверка: обновить установку с D1, где камера явно `armed` и у входа включена тревога; после
  D2 режимы и правила должны остаться (они уже в карте), а `device_settings` — только с именами.
