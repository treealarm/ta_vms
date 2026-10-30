# M2 — спатиальный модуль в `VmsMap` на едином дереве (детальный план)

> Часть плана [Square → ta_vms](README.md); ссылки «§N» ведут в его разделы.
> Файлы — ориентиры, не полный перечень. Шаги отмечаются ✅ по мере исполнения; «Ход работы» —
> в конце.

M2 переносит модуль карт и планов из VmsCfg в `VmsMap` на единое дерево и переключает всех
потребителей (`VmsPtzTrackingSingleton`, web_srv). M1 создал `dotnet/VmsMap` — единственного
владельца базы `ta_map` с деревом `objects`, PostGIS и записью по gRPC, — но пространства живут
до сих пор в VmsCfg: таблицы `space`, `space_plan`, `camera_placement`, `landmark`,
`ptz_geo_observation`, `ground_observation` (`Grpc/migrations/0002…0004`), 25 методов
`SpatialDb.cs`, 21 rpc раздела «Maps & plans» в `proto/vms_cfg.proto` и
`CfgServiceImpl.Spatial.cs`. Это противоречит целевой модели §4.2: пространство — вид узла
дерева, камера после размещения — узел с геометрией и боковой строкой, картинка камеры на
другом плане — реплика, ориентир — тоже узел. Пока модуль в VmsCfg, у карты две базы и два
владельца, а тревоги и права M3 некуда вешать.

Поведение Admin → Maps и операторской карты для браузера **не меняется**: REST-маршруты, DTO,
ETag и форма ошибок остаются прежними.

**Границы.** Не входят: адаптер (`server-config-changed` → узлы камер, состояния и журнал,
§4.4) — это M3; права, `Watch`/SSE, `GetByBox` — M3; фронтенд — M4; переписывание английской
документации — M5. `KMeans` и массовые фигуры остаются как есть. Данные существующих установок
**не переносятся** (§8.2): спатиальные таблицы VmsCfg удаляются, пространства после обновления
пусты.

## Решения

- **Единое дерево.** Пространство — узел `objects` + строка `spaces`; его имя и родитель — в
  `objects`, футпринт — полигон в `geo_objects` (координаты ближайшего предка-холста); ориентир —
  узел + `landmark(height_m)` + точка в `geo_objects`; размещение камеры — узел + строка
  `camera_placement`. Вид узла — наличие боковой строки, а не колонка типа.
- **Камера — один узел с детерминированным id.** `ObjectIdGenerator.GenerateObjectId(camera_id,
  "camera")` (в `Domain/` появляется `CameraObjectId`; M3 использует её же). Опорное размещение —
  этот узел под пространством; его точка — в `geo_objects`, числа наведения — в
  `camera_placement`. Строка `camera_placement` — **на каждое** размещение:
  `object_id PK, camera_id, is_reference, height_m, heading_deg, hfov_deg, range_m`,
  частичный уникальный индекс `(camera_id) WHERE is_reference` — ровно форма §4.2. Лат/лон не
  дублируются, они в геометрии.
- **Картинки — реплики.** Узел-картинка камеры в другом пространстве — реплика
  (`objects.owner_id` = узел камеры), со своей геометрией и своей строкой размещения
  (`is_reference = false`); опорная строка — единственная, по которой наводятся калибровка,
  наведение и master/slave. Правила VmsCfg сохраняются: первое размещение камеры становится
  опорным, снятие флага не продвигает другое, «перенос опорного» оставляет прежнее место
  картинкой (создаётся реплика с прежними значениями), а если в целевом пространстве уже была
  картинка — она уступает место узлу камеры. Так `PlacementPanels.tsx` (isReference, heading,
  hfov, range у каждой картинки) продолжает работать как раньше.
- **Снятие опорного размещения** удаляет строку и точку узла камеры, но сам узел оставляет без
  родителя (неразмещённая камера; M3 подвесит её под узел сервера) — картинки в других
  пространствах остаются живыми репликами. **Удаление пространства** перед удалением поддерева
  так же отцепляет опорные узлы камер, чьи пространства в поддереве, — иначе реплики в других
  пространствах осиротели бы.
- **Картинка плана — `space_plan` с `bytea`** (решение пользователя, 2026-10-30): перенос
  таблицы VmsCfg как есть — `space_id PK → objects ON DELETE CASCADE, mime, width_px, height_px,
  tl/tr/bl lat/lon, etag (sha256 hex), image bytea`; только у пространства вида plan, форматы
  png/jpeg/webp с проверкой сигнатуры, лимит 20 MiB, чанки 256 KiB, `If-None-Match` отдаёт один
  header. Второй склад блобов рядом с `files` принят сознательно: удаление пространства чистит
  картинку каскадом, замена не оставляет сирот, а сервис сохраняет поведение 1:1.
- **`geo_objects.canvas_id`** (uuid, nullable, индекс) — денормализованный ближайший
  предок-холст (`spaces` или `diagrams`), заполняется при каждой записи геометрии M2
  (футпринт, размещения, ориентиры) через `CanvasResolver` (обход предков). Массовые фигуры
  `UpdateFigures` и потребитель `GetByBox` — M3; колонка создаётся сейчас, чтобы не было
  backfill-миграции.
- **Камера проверяется в VmsCfg.** FK `camera_placement.camera_id → camera_setup` исчезает вместе
  с базой; `UpsertCameraPlacement` зовёт `GetCamera` (существование + имя узла) через
  `ICameraCatalog` (§4.4: «осознанная межсервисная проверка вместо FK»). Недоступность VmsCfg —
  `Unavailable`. В `VmsMap` появляется `DAPR_CFG_APP_ID`.
- **Событие одно — `map-config-changed`**, payload
  `{"object_ids": ["…"], "camera_ids": ["…"]}`; публикует `VmsMap` после коммита записи.
  `camera_ids` — камеры, чья пространственная модель изменилась (размещение, опорный флаг,
  наблюдения, ориентир, высота/координаты пространства); пустой список — «сбросить всё».
  `object_ids` — изменённые узлы (пространства, ориентиры, камеры) для будущих подписчиков.
  Ошибка публикации логируется и не валит запись. Из VmsCfg уходят и `space-config-changed`, и
  `server-config-changed` из спатиальных путей — `video_srv`/`VmsFs`/`VmsOnvif` больше не
  делают reconcile ни за чем (побочный плюс из шапки M2).
- **Proto.** Весь раздел «Maps & plans» переезжает в `vms_map.proto`, rpc — в существующий
  `MapService` (один клиент на потребителя); `SourceRequest` заменяется на
  `CameraRequest{camera_id}`; `GeoPoint` — существующим `Coordinate` (DRY); формы `Space`,
  `CameraPlacement`, `Landmark`, `CameraSpatialModelResponse` и остальных сообщений сохраняются,
  поэтому web_srv меняет только клиента и `using`, а REST браузера не меняется вовсе.
- **VmsCfg** теряет модуль целиком: `SpatialDb.cs`, `CfgServiceImpl.Spatial.cs`, DbSet'ы и
  сущности, proto-раздел, DI; миграция `0007_drop_spatial.sql` — `DROP TABLE IF EXISTS` в порядке
  зависимостей. Файлы `0002…0004` остаются историей (мигратор пропускает уже применённые).
- **Ветка** `square-merge/m2-spatial-into-vmsmap` от `master` (после мержа M1); в ta_install
  изменений нет — `DAPR_MAP_APP_ID`, `PUBSUB_NAME`, `DAPR_CFG_APP_ID` и PostGIS уже там с M1.

## Шаги

1. ✅ **`proto/vms_map.proto` — спатиальный раздел.**
   В `service MapService` добавить 21 rpc (порядок и комментарии — как в `vms_cfg.proto`
   105–138): `ListSpaces`, `GetSpace`, `UpsertSpace`, `RemoveSpace`, `UploadSpacePlan`,
   `GetSpacePlanImage`, `SetSpacePlanCorners`, `RemoveSpacePlan`, `ListCameraPlacements`,
   `GetReferencePlacement`, `UpsertCameraPlacement`, `RemoveCameraPlacement`, `ListLandmarks`,
   `UpsertLandmark`, `RemoveLandmark`, `GetPtzGeoObservations`, `SetPtzGeoObservations`,
   `GetGroundObservations`, `SetGroundObservations`, `GetCameraSpatialModel`,
   `ListCameraSpatialModels`. Перенести сообщения `SpaceKind`, `GeoBounds`, `PlanCorners`,
   `SpacePlan`, `Space`, `SpaceRequest`, `GetSpaceResponse`, `ListSpacesResponse`,
   `SpacePlanUploadHeader/Chunk`, `GetSpacePlanImageRequest`, `SpacePlanImageHeader/Chunk`,
   `SetSpacePlanCornersRequest`, `CameraPlacement`, `CameraPlacementRequest`,
   `ListCameraPlacementsResponse`, `GetReferencePlacementResponse`, `Landmark`,
   `LandmarkRequest`, `ListLandmarksResponse`, `PtzGeoObservation(s)`, `GeoObservation`,
   `GroundObservation(s)`, `GeoGroundObservation`, `CameraSpatialModelResponse`,
   `ListCameraSpatialModelsResponse`; новые `CameraRequest{string camera_id = 1}`; `GeoPoint` не
   заводить — везде `Coordinate`. Обновить шапку файла: чтения M2 пришли, дерево и боковые
   строки, событие `map-config-changed`. `vms_cfg.proto` на этом шаге **не трогать** — система
   собирается и работает по-старому.
   Проверка: `dotnet build dotnet/ta_vms.sln` — 0/0 (в VmsMap появились типы `Vms.Map.Space` и
   т.д.).

2. ✅ **`VmsMap` — миграция `Grpc/migrations/0002_spaces.sql`.**
   `spaces(object_id uuid PK → objects ON DELETE CASCADE, kind text, real_coordinates bool,
   ordinal int default 0, elevation_m double default 0, view_south/west/north/east double)`.
   `space_plan(space_id uuid PK → spaces(object_id) ON DELETE CASCADE, mime, width_px,
   height_px, tl/tr/bl lat+lon, etag, image bytea)`. `camera_placement(object_id uuid PK →
   objects ON DELETE CASCADE, camera_id text NOT NULL, is_reference bool default true,
   height_m default 3, heading_deg default 0, hfov_deg default 60, range_m default 30)` +
   `CREATE UNIQUE INDEX … (camera_id) WHERE is_reference` +
   `CREATE INDEX … (camera_id)`. `landmark(object_id uuid PK → objects ON DELETE CASCADE,
   height_m double default 0)`. `ptz_geo_observation(camera_id text, landmark_id uuid →
   landmark(object_id) ON DELETE CASCADE, pan real, tilt real, zoom real NULL, PK(camera_id,
   landmark_id))`; `ground_observation(camera_id text, landmark_id uuid → landmark(object_id)
   ON DELETE CASCADE, x real, y real, PK(camera_id, landmark_id))` — `camera_id` без FK
   (межбазовая проверка вместо неё). `ALTER TABLE geo_objects ADD COLUMN canvas_id uuid NULL` +
   индекс. Комментарии — как в `0002_spatial.sql`, но про дерево.
   Проверка: чистая БД — `VmsMap` применяет `0001` и `0002`, `\dt ta_map` +6 таблиц,
   `\d geo_objects` с `canvas_id`, повторный старт не применяет; старый dev-каталог переживает.

3. ✅ **`VmsMap` — EF-модель и геометрия.**
   `Db/Models/Spatial/{DBSpaces, DBSpacePlan, DBCameraPlacement, DBLandmark,
   DBPtzGeoObservation, DBGroundObservation}.cs` (snake_case, как в M1);
   `PgDbContext` — DbSet'ы и маппинги по образцу `VmsCfgDbContext` (без навигаций, явные LINQ
   join'ы); `DBGeoObject.canvas_id`. Новый `Db/Services/CanvasResolver.cs` (обход предков до
   `spaces`/`diagrams`, LINQ, глубина дерева); `GeoService.CreateGeo` проставляет `canvas_id`;
   `MapUpdateService.UpdateObjects` пересчитывает его у перемещённых узлов, у которых есть
   геометрия. `Domain/CameraObjectId` (обёртка над `ObjectIdGenerator` с версией `"camera"`).
   Проверка: `MapDbTests` по-прежнему зелёные; InMemory-тест на смену холста при переносе.

4. ✅ **`VmsMap` — `Db/Services/SpatialDb.cs` (перенос `SpatialDb.cs`).**
   Переносятся все 25 методов, каждый — одна транзакция записи + уведомление после коммита:
   - `ListSpaces/GetSpace`: `spaces` join `objects` (имя, родитель) left join `space_plan`
     (метаданные без `image`), футпринт — из `geo_objects`; порядок `ordinal, name`.
   - `UpsertSpace`: объект через `IMapUpdateService.UpdateObjects` (id, родитель, имя) + строка
     `spaces` + полигон футпринта; инварианты VmsCfg сохраняются — непустое имя, валидный view
     (SW < NE, без антимеридиана), map только корень и всегда `real_coordinates`, вид не
     меняется, родитель существует и является пространством, запрет на себя/потомка,
     уникальность имени среди соседей-пространств проверяется в коде (DB-индекс не переносится:
     в дереве имена разных видов совпадают).
   - `RemoveSpace`: собрать опорные камеры и камеры с наблюдениями на удаляемых ориентирах,
     отцепить опорные узлы (без родителя, строка и точка удаляются), затем
     `IMapUpdateService.Delete([space])` — поддерево целиком, одной транзакцией.
   - `SavePlan/SetPlanCorners/GetPlanImage/RemovePlan`: `space_plan` как есть (bytea, sha256
     etag, `if_none_match` без чтения байтов), только вид plan.
   - `ListPlacements/GetReferencePlacement/UpsertPlacement/RemovePlacement`: алгоритм из
     «Решений» — первый placement опорный, картинки-реплики, перенос опорного оставляет
     картинку, удаление опорного отцепляет узел; «камера не существует» —
     `SpatialConfigException`; имя узла — из `GetCamera`.
   - `ListLandmarks/UpsertLandmark/RemoveLandmark`: узел + `landmark` + точка; перенос в другое
     пространство запрещён; удаление собирает камеры с наблюдениями.
   - `Get/SetPtzGeoObservations`, `Get/SetGroundObservations`: те же таблицы, замена всего
     набора, проверка «есть опорное размещение» и «пространство ориентира делит геометрию».
   - `GetCameraSpatialModel/ListCameraSpatialModels`: сборка моделей из узла камеры,
     пространства, ориентиров-узлов и наблюдений; `height_above_ground_m = elevation_m +
     landmark.height_m`, `floor_elevation_m = elevation_m`; фильтр по общему пространству/реальным
     координатам — те же два запроса, что сегодня.
   Проверка: `dotnet build`; перенос сверяется с `SpatialDb.cs` построчно (все 25 методов на
   месте, `GetReferencePlacement` сохраняется несмотря на отсутствие вызовов).

5. ✅ **`VmsMap` — клиент VmsCfg и публикация.**
   `Clients/ICameraCatalog.cs` + `Clients/VmsCfgCameraCatalog.cs` (`GetCamera` через
   `DaprClient.CreateInvocationInvoker(DAPR_CFG_APP_ID)`, возвращает id/имя, ошибка →
   `RpcException(Unavailable)`); `PubSub/IMapConfigNotifier.cs` +
   `PubSub/DaprMapConfigNotifier.cs` (`PublishEventAsync(PUBSUB_NAME, "map-config-changed",
   {object_ids, camera_ids})`, warning при сбое); `Program.cs` — `DAPR_CFG_APP_ID` и
   `PUBSUB_NAME` обязательны, регистрация клиента/каталога/нотифаера, `Protobuf
   Include="..\..\proto\vms_cfg.proto" GrpcServices="Client"` в `VmsMap.csproj`.
   Проверка: старт без переменной падает с внятным сообщением (no silent fallback).

6. ✅ **`VmsMap` — `Grpc/MapServiceImpl.Spatial.cs`.**
   `MapServiceImpl` становится `partial`; новый файл — тонкий слой proto ↔ DTO: 21 override,
   `SpatialConfigException` → `InvalidArgument`/`OperationStatus{success=false,message}`,
   потоковая загрузка плана (первое сообщение — header, проверка mime по сигнатуре, лимит
   20 MiB, чанки 256 KiB) и выдача (`if_none_match` → header `not_modified`).
   `PlanImageFormats` переезжает сюда же.
   Проверка: `dotnet build`; gRPC-поверхность совпадает с `vms_cfg.proto` 1:1 по формам.

7. ✅ **`VmsMap.Tests`.**
   InMemory (каталог и нотифаер — фейки): пространство-объект и вложенный план с футпринтом;
   инварианты (map только корень, вид не меняется, дубль имени среди соседей, перенос в потомка);
   размещение — детерминированный id узла, первый placement опорный, картинка-реплика, перенос
   опорного, снятие флага, удаление опорного/картинки; удаление пространства отцепляет опорный
   узел и чистит поддерево; ориентиры и запрет переноса; наблюдения и правило общей геометрии;
   модель камеры (высоты, фильтр); план (etag, `if_none_match`, corners, remove, не-plan);
   `canvas_id` при вставке и переносе; ошибка «камеры нет». Postgres-тест (DSN, как в
   `PostgresTests`): рекурсивное удаление поддерева, каскады, частичный уникальный индекс
   опорного, PostGIS-точка/полигон, `canvas_id` SQL-путём.
   Проверка: `dotnet test dotnet/VmsMap.Tests` (без DSN и с DSN).

8. ✅ **`VmsPtzTrackingSingleton`.**
   `Geo/CameraSpatialSource.cs` → `MapService.MapServiceClient`, `CameraRequest`, `Vms.Map.*`;
   `Geo/CameraSpatialModel.cs` — `Vms.Map.Space`; `CameraConfigSubscriber.cs` — подписка на
   `map-config-changed` (payload `camera_ids`, пусто = всё; разбор с безопасным откатом в «всё»),
   `space-config-changed` убирается; `server-config-changed` остаётся для конфигурации камер;
   `Program.cs` — `DAPR_MAP_APP_ID` обязателен и клиент; `VmsPtzTrackingSingleton.csproj` —
   `Protobuf Include="..\..\proto\vms_map.proto" GrpcServices="Client"`; комментарии
   `SpatialModelCache` — про новый топик; два теста (`CameraSpatialModelTests`, `GeoAimingTests`)
   переводятся на `Vms.Map`.
   Проверка: `dotnet test dotnet/VmsPtzTrackingSingleton.Tests` — 96 зелёных.

9. ✅ **`web_srv.Server`.**
   `web_srv.Server.csproj` — `vms_map.proto` `GrpcServices="Client"`; `Program.cs` —
   `DAPR_MAP_APP_ID` и `MapService.MapServiceClient`; `Controllers/SpatialController.cs`,
   `SpacePlanController.cs`, `GroundGeoController.cs`, `PtzGeoController.cs`, `SpatialMapper.cs`
   — спатиальные rpc через клиента карты, `Vms.Map`, `CameraRequest`; DTO, маршруты, ETag,
   `{error}` не меняются; клиент трекинга и проверки прав — как были.
   Проверка: `dotnet build`; браузерные запросы Admin → Maps и оператора идут по тем же URL.

10. ✅ **`VmsCfg` — демонтаж модуля.**
    Удалить `Grpc/SpatialDb.cs`, `Grpc/CfgServiceImpl.Spatial.cs`, DbSet'ы/маппинги/сущности из
    `VmsCfgDbContext`, `AddSingleton<SpatialDb>()` из `Program.cs`, `spatialDb` из конструктора
    `CfgServiceImpl`, спатиальный раздел из `proto/vms_cfg.proto` (без `reserved` — правило
    CLAUDE.md). Миграция `Grpc/migrations/0007_drop_spatial.sql`:
    `DROP TABLE IF EXISTS ground_observation, ptz_geo_observation, landmark, camera_placement,
    space_plan, space;` (файлы `0002…0004` остаются).
    Проверка: `dotnet build`/`dotnet test` (VmsCfg.Tests — 26); на dev-БД после старта
    `\dt` без спатиальных таблиц; на чистой БД их не появляется.

11. ✅ **Конфигурация.**
    `.vscode/launch.json`: `DAPR_MAP_APP_ID="vmsmap"` в «Backend (.NET)» и
    «VmsPtzTrackingSingleton (.NET)»; `DAPR_CFG_APP_ID="vmscfg"` и `PUBSUB_NAME="orderpubsub"`
    в «VmsMap (.NET)». `.env` и `../ta_install/.env` уже содержат `DAPR_MAP_APP_ID`,
    `DAPR_CFG_APP_ID`, `PUBSUB_NAME` (M1) — правок не требуют; `services.json`/`windows.env` —
    без отличий (переменные приходят из общего env). Проверить это, а не переписывать.

12. ✅ **Документация.**
    `CLAUDE.md` — раздел «Maps & plans»: владелец `VmsMap`, убрать «are moving into a map service»,
    событие `map-config-changed` вместо `space-config-changed`, строка про реплики-картинки и
    детерминированный узел камеры; в «Inter-service communication» — оговорка, что дерево,
    пространства и размещения принадлежат `VmsMap`, VmsCfg остаётся источником конфигурации
    камер. `docs/maps-and-plans.md` — два абзаца («Where things live», «Events») приводятся к
    новому владельцу; полная переработка модели — M5. В этом файле — «Ход работы» после
    исполнения.

**Проверка:** `dotnet build dotnet/ta_vms.sln` — 0 ошибок, 0 предупреждений; `dotnet test
dotnet/ta_vms.sln` — все проекты (VmsMap, VmsCfg, VmsFs, VmsAnalyticsSingleton,
VmsPtzTrackingSingleton), плюс `VMS_MAP_TEST_DSN` на `postgis/postgis:latest`. **F5**: компаунд
`Full stack (F5)` поднимает весь стек, включая `VmsMap`; ни один сервис не падает на
обязательных переменных. Admin → Maps в браузере: карта-корень и вложенный план, загрузка
картинки (ETag, повторный GET — 304), ориентиры, опорное размещение и картинка камеры на другом
плане с правкой heading/hfov/range, снятие опорного флага; дерево, футпринты и хлебные крошки
как раньше; операторская карта — наведение по карте. Стенд `tests/onvifsim`
(`onvifsim_lab.py`): `provision`/`calibrate`/`aim-check`/`bind` на парах map и plan —
калибровка по ориентирам, наведение по карте, master/slave методом `geo`, отказ «shares no
coordinates» между несвязанными пространствами. `grpcurl` через Dapr-сайдкар по
`proto/vms_map.proto`: `UpsertSpace`, `GetSpace`, `ListSpaces`, `UploadSpacePlan` (два чанка),
`GetSpacePlanImage`, `UpsertCameraPlacement`, `GetCameraSpatialModel`, `SetPtzGeoObservations`,
`RemoveSpace`; в `ta_map` — узлы, `spaces`, `camera_placement`, `landmark`, наблюдения,
`canvas_id`. События: правка размещения/наблюдений даёт `map-config-changed` с `camera_ids`,
правка пространства/ориентира — пустой `camera_ids`; `grep -rn space-config-changed` — только
история в планах; `video_srv`/`VmsFs`/`VmsOnvif` не логируют reconcile после спатиальных
записей. `grep -rn "Vms\.Cfg\.\(Space\|CameraPlacement\|Landmark\|PtzGeoObservation\|
GroundObservation\)"` — пусто; `dotnet/VmsCfg/Grpc` без `SpatialDb`/`Spatial`.

**Критерии готовности:** VmsCfg без спатиальных таблиц, кода и proto-раздела; `VmsMap` — владелец
дерева, пространств, планов, размещений, ориентиров, наблюдений и моделей камеры; Admin → Maps и
операторская карта работают без изменений в браузере; стенд `tests/onvifsim` проходит
калибровки, наведение и master/slave через `VmsMap`; `VmsPtzTrackingSingleton.Tests` и
`VmsCfg.Tests` зелёные; полный dev-стек поднимается по F5; `map-config-changed` заменил
`space-config-changed`, и спатиальные записи больше не будят `video_srv`/`VmsFs`/`VmsOnvif`.

**Известные ограничения до M3:** удаление камеры в VmsCfg не убирает её узел и размещения (FK
каскада больше нет) — это сделает адаптер M3; неразмещённая камера живёт узлом без родителя, пока
адаптер не подвесит её под узел сервера; `canvas_id` для массовых `UpdateFigures` заполняется в M3
вместе с `GetByBox`.

## Ход работы

Ветка `square-merge/m2-spatial-into-vmsmap` в ta_vms, от `master` `233f215` (мерж M1). В ta_install
ветка не нужна: M1 уже положил туда `vmsmap`, PostGIS и `DAPR_MAP_APP_ID`, а M2 других изменений
там не делает.

Сделано 2026-10-30, по коммиту на кусок:

| Коммит | Что |
|---|---|
| `18d1d26` | этот план |
| `fcf8297` | спатиальный раздел в `vms_map.proto` (21 rpc, `CameraRequest`, `Coordinate`) |
| `ea998e8` | миграция `0002_spaces.sql`, EF-модель, `CanvasResolver`, `CameraObjectId` |
| `5d37d13` | `SpatialDb`, каталог камер, `map-config-changed`, gRPC-слой, тесты |
| `993f0b9` | PtzTracking читает `VmsMap` и подписан на `map-config-changed` |
| `1ac20e1` | web_srv переключён на `MapService` |
| `3eba3ca` | демонтаж модуля в VmsCfg и миграция `0007_drop_spatial.sql` |
| `0674ada` | data source `VmsMap` строится после миграции (PostGIS на чистой БД) |
| `3f3182b` | `launch.json`, `CLAUDE.md`, `docs/maps-and-plans.md` |

**Решения по ходу, которых не было в плане:**
- `SpatialDb` — `public`: его внедряет публичный `MapServiceImpl`; вместе с ним публичны
  `ICameraCatalog` и `IMapConfigNotifier`.
- `CanvasResolver` — статический помощник над `VmsMapDbContext`, а не внедряемая зависимость:
  иначе пришлось бы менять конструктор `MapDb` и ручную сборку сервисов в тестах.
- Транзакции в `SpatialDb` открываются только на реляционном провайдере (`IsRelational()`):
  InMemory в тестах транзакций не имеет, production всегда PostgreSQL. Уведомление публикуется
  после коммита, внутри `SpatialDb` — чтобы будущий внутренний вызывающий не забыл его.
- `RemoveSpace` отцепляет опорные узлы камер до удаления поддерева; удаление и пересчёт тревог
  идут через `MapUpdateService.Delete`, поэтому этот путь проверяется только на Postgres, как и
  рекурсивный CTE.
- Опорная строка размещения всегда принадлежит узлу камеры; при переносе узел переезжает вместе
  со строкой, а прежнее место получает реплику-картинку с копией значений. Операции над
  картинками (создание, удаление) — прямые EF-удаления узла, строки и точки.
- `GetReferencePlacement` сохранён, хотя вызовов в репозитории нет: он часть контракта.
- Postgres-тесты `VmsMap.Tests` собраны в xUnit-коллекцию `postgis`: два класса делят одну
  scratch-БД и делают `TRUNCATE`, параллельно они мешали друг другу.

**Проверено 2026-10-30:**
- `dotnet build dotnet/ta_vms.sln` — 0 ошибок, 0 предупреждений; `dotnet test` — VmsMap 41
  (из них 9 Postgres-тестов с `VMS_MAP_TEST_DSN`), VmsCfg 26, VmsFs 5, VmsAnalyticsSingleton 24,
  VmsPtzTrackingSingleton 96.
- Чистая `ta_map`: миграции `0001` + `0002`, шесть новых таблиц, `geo_objects.canvas_id`;
  футпринт вложенного плана — `ST_Polygon`, его `canvas_id` — родительская карта; повторный
  старт миграцию не применяет.
- Dev-база `ta_cfg`: `0007_drop_spatial.sql` применена, спатиальных таблиц не осталось.
- Полный dev-стек (все 13 сервисов через `scripts/dev_stack.py`, env из тех же `launch.json`/
  `dapr.yaml`, что у F5) поднялся и слушает; ни один сервис не упал на обязательных переменных.
- REST-сценарий Admin → Maps через web_srv: карта-корень, вложенный план с футпринтом, загрузка
  плана (ETag, повторный GET — 304), опорное размещение, ориентир, наблюдения PTZ (модель
  посчитана, расстояние 139 м), удаление пространства — камера отцеплена (узел без родителя),
  размещений нет.
- `map-config-changed`: пока PtzTracking был остановлен, запись в карту оставила в очереди
  `vmsptztrackingsingleton-map-config-changed` ровно одно сообщение; после старта очередь
  опустела. В логах VmsMap и Ptz — ни одного warning о публикации.
- `video_srv` пересобран против урезанного `vms_cfg.proto` — сборка чистая.
- Стенд `tests/onvifsim` (`onvifsim_lab.py all`), вход 0, обе пары (map и plan), крепление
  `level` — все шаги:
  - **provision**: камеры `sim-master`, `sim-ptz`, `sim-master2`, `sim-ptz2` добавлены через
    ONVIF/`Profile_2`; созданы «OnvifSim lab» (карта, реальные координаты) и «OnvifSim lab plan»
    (план, вымышленные), по 5 ориентиров и по 2 опорных размещения; картинка плана 1280×960
    (31 KiB, 96 м с 120 м); боксы человека дошли из обеих мастер-камер через VmsOnvif и
    аналитику.
  - **calibrate**: PTZ по 5 ориентирам — худший leave-one-out остаток 0.00000 на обеих парах;
    расстояния до ориентиров совпали с миром в пределах 0.1 м; 5 zoom-сэмплов на пару записаны;
    наземные отметки — худший остаток 0.058 м (карта) и 0.023 м (план).
  - **aim-check** (60 с): `POST /api/ptz-geo/{id}/aim` — ошибка наведения 0.000° на обеих парах,
    lag 0.72° / 0.57° в среднем; человек целиком в кадре 24/24; промах по zoom 0.0000.
  - **bind**: обе привязки методом `geo` приняты, сессии пошли (`active`, `masterCameraId`
    верный); для пары плана отказы сработали — привязка `sim-ptz2` к `sim-master` отклонена
    «shares no coordinates», наведение в точку карты — 400.
  - **run** (60 с): автопроводка — arrival 0.23° / 0.21° в среднем (p95 0.41° / 0.44°),
    tracking p95 4.2° / 2.5°; человек в кадре 236/237 и 237/237; медианная доля высоты 0.38;
    по 28 команд.
  - После прогона в `ta_map`: 2 пространства (карта с реальными координатами, план — без), у
    плана строка `space_plan`, 4 опорных размещения и ни одной реплики, 10 ориентиров, по 10 PTZ-
    и наземных наблюдений, 14 геометрий с `canvas_id`, и он указывает на своё пространство-холст;
    в `ta_cfg` спатиальных таблиц 0; `vms`/`vmsfs`/`vmsonvif` reconcile не логировали; очередь
    `vmsptztrackingsingleton-map-config-changed` существует и пуста (0 сообщений, 1 consumer); в
    логах VmsMap и Ptz — ни одного warning.

**Не проверено:**
- Windows: `Build-Stage.ps1`, `services.json` и установка (в M2 их не меняли; env приходит из
  общего `.env`).
- Контейнерная сборка `vmsmap` и публикация образа.
- Литеральный F5 в VS Code (стек поднимался `dev_stack.py` с теми же env и портами) и ручная
  работа браузера — проверены REST-вызовы, которыми браузер и пользуется.

