# M3 — состояния, события, права, треки, значения, мнемосхемы: `VmsMap` → web_srv (объём; детали — при старте)

> Часть плана [Square → ta_vms](README.md); ссылки «§N» ведут в его разделы.
> Файлы — ориентиры, не полный перечень.

1. Адаптер (§4.4) из кода `VmsSquareSingleton` (взять из истории git: последний коммит с ним —
   `37c6bc6` на `master`, `git show 37c6bc6:dotnet/VmsSquareSingleton/<файл>`): `CameraChangeSubscriber`,
   `SquareAlarmSubscriber`, `SquareResyncService` → `VmsMap/Adapter/*`, без gRPC в Square. Узел
   сервера для неразмещённых камер.
2. Агрегация тревог (§3, строка `BlinkService`) в `StatesUpdateService.UpdateStates`.
3. `MapService` (vms_map.proto), чтение: объекты (`GetByParent`, `GetByIds`, `GetByBox` с
   кластерами и слоями, `GetByName`, `GetByParams`, `CreateReplica`, `Delete`), состояния
   (`GetVisualStates`, `GetAlarmedStates`, справочник), события (`GetByFilter` с поддеревом и
   keyset-пагинацией), треки, значения, мнемосхемы и типы, права, уровни, файлы, `Watch`.
   web_srv: контроллеры `api/map/objects|states|events|tracks|values|diagrams|rights|levels|
   files`, `api/map/live` (SSE).
4. Права (§4.7): `rights` с наследованием и `GetEffectiveRights` в `VmsMap`; в web_srv
   `CameraAccessFilter`/`CameraAccessService` → `RightsFilter`/`RightsService` (94 атрибута
   переименовываются механически, семантика флагов та же); `api/permissions*` и админская
   страница прав переключаются на `VmsMap`; VmsCfg: миграция удаляет три таблицы прав, rpc и
   `cam-perm-version` уходят. Раздел «Login» в `CLAUDE.md` правится здесь же (права — на узлах
   дерева в `VmsMap`, не «в VmsCfg»).
5. `api/fs/alarms` и `useAlarms` остаются до M4, затем удаляются.

Проверка: `VmsMap.Tests`; тревога от `RetainEvent` (стенд ONVIF-событий из
`docs/test-camera-from-file.md`/`tests/onvifsim`) появляется состоянием `ALARM` у камеры и
`children_alarms` у её пространства и здания в `GetVisualStates`, через SSE приходит
`set_visual_states`, по окончании снимается; событие — в `GetByFilter`; `camera-state-changed` →
`armed`; права: пользователь без доступа к камере не видит её узел, состояния и события; право
`view_live` на здании открывает live всех его камер; все существующие проверки web_srv (live,
архив, PTZ, экспорт) ведут себя как до переезда — это проверяется по тем же сценариям, что при
введении `CameraAccess`.
