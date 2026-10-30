# M2 — спатиальный модуль переезжает в `VmsMap` на единое дерево (объём; детали — при старте)

> Часть плана [Square → ta_vms](README.md); ссылки «§N» ведут в его разделы.
> Файлы — ориентиры, не полный перечень.

1. Миграция `0002_spaces.sql`: `spaces`, `space_plan`, `camera_placement`, `landmark`,
   `ptz_geo_observation`, `ground_observation`, `geo_objects.canvas_id`.
2. Перенос `SpatialDb.cs` и `CfgServiceImpl.Spatial.cs` на новые таблицы в `VmsMap` (пространство
   — объект; футпринт — фигура; размещение — объект + боковая строка; картинка — реплика). Раздел
   «Maps & plans» из `proto/vms_cfg.proto` → `proto/vms_map.proto`, включая
   `GetCameraSpatialModel`/`ListCameraSpatialModels` и чанковые планы.
3. VmsCfg: миграция, удаляющая спатиальные таблицы; `DbSpace` и прочие сущности, proto-раздел и
   `SpatialDb` удаляются. Топик `space-config-changed` уходит; `VmsMap` публикует
   `map-config-changed {"object_ids": [...], "camera_ids": [...]}` (пространство — объект,
   поэтому один топик; оба списка пустые — «всё»). `camera_ids` нужны потому, что
   `SpatialModelCache` в `VmsPtzTrackingSingleton` адресуется по `camera_id` и сопоставить объект
   с камерой без похода в `VmsMap` не может. Побочный плюс: сегодня `RemoveSpace`,
   `UpsertCameraPlacement`, `Set*Observations` публикуют `server-config-changed` и заставляют
   `video_srv`/`VmsFs`/`VmsOnvif` делать reconcile ни за чем (`CfgServiceImpl.Spatial.cs`
   признаёт это неудобством) — после переезда этого не будет.
4. Подписчики: `VmsPtzTrackingSingleton` — `Geo/CameraSpatialSource.cs` получает клиента `VmsMap`
   (`DAPR_MAP_APP_ID`), `CameraConfigSubscriber.cs` слушает `map-config-changed` (инвалидация по
   `camera_ids`, сброс всех при пустом) вместо `space-config-changed`; web_srv
   `SpatialController`, `SpacePlanController`, `GroundGeoController`, `PtzGeoController`
   переключаются на клиента `VmsMap`, REST для браузера не меняется. `DAPR_MAP_APP_ID` нужен
   **каждому** web_srv, включая узловые (`docker-compose.distributed.yml`, роль node в
   `services.json`).
5. `CLAUDE.md` правится **в этом же майлстоуне**, не в M5: «Maps & plans: positions belong to
   ta_vms» (владелец — `VmsMap`), `space-config-changed` → `map-config-changed`, строка про
   VmsCfg как единственный источник конфигурации получает оговорку про карту. Иначе между майлстоунами
   правила противоречат коду.
6. Данные существующих установок (v0.1.x, тестовые) **не мигрируются**; это записывается в
   `docs/plans/square-merge/README.md` и в release notes `../ta_install`. (Допущение; см. §8.)

Проверка: `VmsPtzTrackingSingleton.Tests`, `VmsCfg.Tests` зелёные; стенд `tests/onvifsim`
(`onvifsim_lab.py`) проходит калибровку по ориентирам, наведение по карте и пару master/slave
методом `geo` — тот же набор, что в `docs/maps-and-plans.md`; Admin → Maps работает без изменений в
браузере.
