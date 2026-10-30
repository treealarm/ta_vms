# M3 — состояния, события, права, треки, значения, мнемосхемы: `VmsMap` → web_srv (детальный план)

> Часть плана [Square → ta_vms](README.md); ссылки «§N» ведут в его разделы.
> Файлы — ориентиры, не полный перечень. Шаги отмечаются ✅ по мере исполнения; «Ход работы» —
> в конце.

M1 создал `VmsMap` — владельца `ta_map` с деревом, состояниями, событиями, правами и записью по
gRPC. M2 перенёс туда пространства, планы, размещения и калибровки. Но читать это дерево снаружи
пока нечем: в `vms_map.proto` есть только запись и спатиальный раздел, адаптера, который превращает
жизнь ta_vms в состояния и журнал, нет, а права по-прежнему живут в VmsCfg отдельной системой
`user_camera_permission`.

M3 закрывает эту дыру:

- **адаптер** внутри `VmsMap` переводит `server-config-changed`, `camera-state-changed`,
  `retention-event-created` и готовность путей MediaMTX в узлы дерева, состояния и события;
- **чтение** дерева, состояний, событий, треков, значений, мнемосхем, прав, уровней и файлов
  становится контрактом `MapService`, а `watch` — потоком изменений;
- **права** становятся одной моделью на систему: строки на узлах дерева с наследованием вниз,
  `VmsMap` — их владелец; `CameraAccess*` в web_srv переименовывается в `Rights*` и читает права
  оттуда же;
- **web_srv** получает `api/map/*` и SSE `api/map/live`; старые проверки live, архива, PTZ и
  экспорта ведут себя как до переезда.

Поведение браузера до M4 не меняется: `api/fs/alarms` и `useAlarms` остаются, `api/permissions*`
сохраняет прежние DTO, а новая админка прав и операторские экраны — M4.

**Границы.** Не входят: фронтенд (§4.8) — M4; переписывание английской документации — M5;
внешние источники объектов и команд для не-камерных объектов (`docs/future_plans`); удаление
`api/fs/alarms` — M4. Windows-инсталлятор M3 не меняет: новых сервисов и обязательных переменных
нет, `vmsmap` и его env там уже есть с M1.

## Решения

- **Ветка** `square-merge/m3-states-events-rights` от `master` после быстрого переноса M2
  (`2bd5e15`). В `../ta_install` изменений нет.
- **Камера — узел дерева с детерминированным id** (`CameraObjectId.For`, M2). Размещённая камера
  висит под пространством своего опорного размещения — это уже делает `SpatialDb`. Неразмещённая
  висит под **узлом сервера**: обычным узлом без боковой строки, id — `ObjectIdGenerator` с
  версией `"server"`, имя — `server_id` (в `ListMediaServers` имени сервера нет). Узел сервера
  создаёт адаптер; корневых узлов может быть несколько, синтетического корня не заводим.
- **Адаптер идемпотентен и живёт в каждой реплике.** Стартовая сверка повторяет
  `ListCameras`/`ListMediaServers`, пока не удастся; периодическая сверка (раз в 5 с, вместе с
  проверкой связи) восстанавливает родителя камеры: опорное размещение → его пространство, иначе
  → узел сервера. Так снятие опорного размещения (M2 оставляет узел без родителя) чинится само,
  без отдельного события.
- **Удаление камеры** (`GetCamera.Found=false`) удаляет её узел, все реплики (`owner_id`), строки
  `camera_placement` и наблюдения по `camera_id` — одной транзакцией. Межбазовой FK больше нет,
  это замена `ON DELETE CASCADE`.
- **Тревога.** `retention-event-created` — только адресация: адаптер дочитывает эпизод через
  `FsService.ListRetentionEvents` (`camera_id`, `from_alarm_start_ts`) и одной транзакцией пишет
  состояние `ALARM` на узел камеры и событие `alarm` (`param0=stream`, `param1=trigger`,
  `alarm_end_ts` в свойствах). Id события — `event_uuid` эпизода: повторная доставка Dapr не
  плодит дубли. `NORM` ставит периодическая сверка раз в 3 с: объекты в `ALARM`, чей конец в
  прошлом, перепроверяются в БД (эпизод мог продлиться), и только потом снимаются. Список идущих
  тревог — в БД, не в памяти реплики; сверка идемпотентна.
- **Связь** `CONNECTED`/`DISCONNECTED`: топика «камера в сети» нет. Адаптер раз в 5 с зовёт
  `VmsUtils.GetMediaMtxPathsInfo` одним запросом на все камеры (не на камеру и не на сервер) и
  выставляет состояние по `has_source`. Пишет только при смене.
- **Запись состояний камеры** — одна операция «прочитать текущий набор, добавить/убрать, записать
  целиком» под `pg_advisory_xact_lock(hashtext('ta_map_camera_state'))`: иначе тревога и связь,
  придя одновременно, затрут друг друга. Чужие состояния (например, `INFO` от внешнего
  источника) сохраняются.
- **Права — строки на узлах дерева** `rights(object_id, subject, action)`.
  - `subject` — роль Keycloak или имя пользователя; проверяются и роли, и имя.
  - `action` — открытый набор. Общие для любого узла: `view`, `edit`. Камера дополнительно:
    `view_live`, `view_archive`, `ptz`, `control`, `export`. Таблица «вид узла → действия» —
    `RightActions` в `VmsMap`, её же отдаёт `GetRightActions` для UI.
  - Наследование **вниз**: право на здании действует на этажи и камеры, на узле сервера — на его
    неразмещённые камеры, на корне — на всё поддерево. `admin` имеет всё без строк.
  - Режим один на систему: `open` — узел без строк и без строк-предков виден всем и получает все
    действия своего вида; `enforced` — только по строкам. `admin` и режим `open` — два разных
    источника «всё разрешено».
  - Владелец — `VmsMap`. `rights-version` в state store (`DAPR_STATE_STORE_NAME`) меняется на
    каждой записи прав или режима — тот же механизм, что `cam-perm-version`, потому что web_srv
    реплицируем, а не один.
  - **Вычисление эффективных прав — в `VmsMap`**, одна реализация: `GetEffectiveRights` отдаёт
    режим, признак `admin` и развёрнутые по дереву `{object_id, camera_id?, actions[]}` для
    строк, попавших под субъект. В `open` узлы без строк в ответ не попадают — web_srv знает
    режим и сам считает их разрешёнными; в `enforced` — не разрешены.
  - **Фильтрация чтений — в `VmsMap`**: `GetByParent`, `GetByIds`, `GetByBox`, `GetByName`,
    `GetByParams`, `GetVisualStates`, `GetAlarmedStates`, события, треки, значения и `Watch`
    получают `Caller{username, roles, unrestricted}` и отбрасывают узлы без `view`. Правило одно,
    реализаций две: вычисление прав — в `GetEffectiveRights`/`RightsResolver`, применение — в
    каждом чтении. web_srv только передаёт контекст.
  - **Совместимость на один майлстоун.** `api/permissions*` сохраняет прежние DTO
    (`UserCameraPermissions` с флагами). Запись `ReplaceForUser` переводит флаги в строки прав на
    узле камеры, добавляя `view` к любому ненулевому флагу (видимость камеры в дереве — `view`).
    `AllCameras` — строки на всех корневых узлах (карты и узлы серверов): новые камеры под ними
    наследуют право. Чтение собирает обратно: `AllCameras=true`, если у всех корней есть все пять
    действий; иначе — по камерам. Старая админская страница работает без изменений до M4.
- **`Watch` — один поллер на процесс.** Все стримы складывают наблюдаемые наборы; раз в секунду
  поллер читает объединение (геометрия по боксам, состояния и тревоги по id, значения при
  запросе) и диффует по `Version` (xmin). Сообщения повторяют Square:
  `set_ids2update`, `set_ids2delete`, `set_visual_states`, `on_values_changed`; каждое несёт
  `object_id` и `camera_id` для узла-камеры. Права применяются к каждому сообщению. При росте
  дерева — таблица изменений вместо перечитывания (`docs/future_plans`).
- **`GetByBox`** сохраняет поведение Square: до 1000 фигур — как есть, больше — сортировка по
  слоям (`ZoomComparer`) и кластеры `KMeans` (переносим из Square вместе с `KMCentroid`), первые
  500 — без кластеризации.
- **Файлы** уже в `files` и отдаются `GetFile`; M3 добавляет только HTTP — `api/map/files/{id}`
  чанками, лимит 20 MiB.
- **VmsCfg теряет права целиком**: `PermissionDb`, DbSet'ы и сущности, четыре rpc, константу
  `cam-perm-version` и `BumpCameraPermissionsVersionAsync`. Миграция
  `0008_drop_permissions.sql` удаляет `user_camera_permission`, `user_all_cameras_permission`,
  `camera_acl_setting`. Данные не переносятся (установки тестовые); при желании — одна SQL-вставка
  в `rights`, форма флагов сохранена один к одному.
- **Proto** — без `reserved` (правило CLAUDE.md). Чтения получают отдельные request-сообщения с
  `Caller`, чтобы фильтрация была частью контракта, а не соглашением.

## Шаги

1. ✅ **`proto/vms_map.proto` — чтения, права и `Watch`.**
   Добавить в `service MapService`:
   - объекты: `GetByParent(GetByParentRequest)`, `GetByIds(GetByIdsRequest)`,
     `GetMarkersByIds(GetByIdsRequest)`, `GetByBox(GetByBoxRequest)`,
     `GetByName(GetByNameRequest)`, `GetByParams(GetByParamsRequest)`, `GetObjProps`,
     `CreateReplica(CreateReplicaRequest)`;
   - состояния: `GetStates`, `GetVisualStates`, `GetAlarmedStates`, `GetStateDescriptions`;
   - события: `GetEventsByFilter`;
   - треки: `GetTracksByFilter`, `GetTracksByBox`;
   - значения: `GetValuesByOwners`;
   - мнемосхемы: `GetDiagramContent`, `GetDiagramsByIds`, `GetDiagramTypes`;
   - уровни: `GetLevels`, `GetLevelByZoom`;
   - права: `GetRights`, `GetEffectiveRights`, `GetRightActions`, `GetRightsMode`,
     `SetRightsMode`;
   - `Watch(stream WatchRequest) returns (stream WatchMessage)`.
   Сообщения: `Caller{username, roles, unrestricted}`, `GetByParentRequest/Response`
   (`parents`, `children`, `start_id`, `end_id`), `Marker` (+`has_children`, `camera_id`),
   `GetByBoxRequest{Box, Caller, property_filter}`, `Box{wn, es, zoom, ids, count, zone,
   not_in_zone}`, `SearchFilter`, `SearchEventFilter`, `VisualStates{alarmed_objects, states,
   states_descr}`, `DiagramContent{children, content, dgr_types, parents}`,
   `NodeRights{object_id, camera_id, actions}`, `EffectiveRightsResponse{mode, admin, nodes}`,
   `RightActionTable`, `WatchRequest{set_box|set_ids|update_values, Caller}`,
   `WatchMessage{action, items[{object_id, camera_id}]}`. Формы DTO — из `VmsMap/Domain` и
   Square, без `Proto`-префиксов.
   Проверка: `dotnet build dotnet/ta_vms.sln` — 0 ошибок, 0 предупреждений.

2. ✅ **`VmsMap` — права: модель, наследование, режим, версия.**
   `Domain/Rights/RightActions.cs` (вид узла → набор действий; вид — наличие боковой строки:
   `spaces`, `landmarks`, `camera_placement`, `diagrams`, `geo_objects`, иначе `object`).
   `Domain/Rights/NodeRightsDTO.cs`. `Db/Services/RightsService.cs` (рядом с `RightService`):
   - `GetRights(ids)` — строки как есть;
   - `GetEffectiveRights(username, roles, unrestricted)` — обход вниз от узлов со строками
     субъекта, объединение действий, `open`/`enforced`, `admin`; обход по уровням, без CTE —
     глубина дерева мала, а тот же код работает на InMemory;
   - `GetNodeActions(ids, caller, action)` — для фильтрации чтений: обход вверх от каждого id,
     строки на узлах, предках и владельце реплики, один запрос на уровень;
   - `GetMode/SetMode`, `GetActionTable`;
   - `IMapRightsNotifier` (`DaprRightsVersionNotifier`) — `DAPR_STATE_STORE_NAME`, ключ
     `rights-version`; ошибка публикации логируется и не валит запись.
   `RightsService` дополняет, а не заменяет `IRightServiceInternal` (тот остаётся слоем строк);
   `UpdateRights`/`DeleteRights` в `MapServiceImpl` бампают версию. Режим хранится в новой таблице
   `rights_mode` (миграция `0003_rights.sql`, старт — `enforced`).
   Проверка: InMemory-тесты — наследование здание→этаж→камера, роль и пользователь, `open` vs
   `enforced`, `admin`, `unrestricted`, таблица действий, реплика получает действия владельца;
   Postgres-тест наследования — вместе с шагом 3.

3. **`VmsMap` — gRPC-чтение.**
   `Grpc/MapServiceImpl.Reads.cs` (partial): тонкий слой proto ↔ DTO поверх существующих
   `MapDb`, `StateService`, `EventsService`, `TrackService`, `ValuesService`, `DiagramService`,
   `DiagramTypeService`, `LevelService`, `FileService`, `GeoService`. Все методы с `Caller`
   фильтруют по `view` через `RightsService.GetNodeActions`; `GetVisualStates` и
   `GetAlarmedStates` считают состояния владельца для реплик (как Square). `GetByIds`/`GetByBox`
   возвращают `Figures` с геометрией; `GetByBox` переносит `KMeans`/`KMCentroid` из Square
   (`LeafletAlarms/KMeans/`) и `ZoomComparer`. `CreateReplica` создаёт узел с `owner_id`,
   копирует свойства и visual владельца. `GetDiagramContent` — глубина 0–3, дети, типы, родители.
   `GetObjProps` — marker + свойства + `zoom_min/max`.
   Проверка: `dotnet build`; InMemory-тесты на фильтрацию (камера без `view` не видна, право на
   здании открывает камеру), `GetByBox` с кластерами, пагинацию событий по keyset, `CreateReplica`.

4. **`VmsMap` — адаптер: узлы камер и удаление.**
   `Adapter/CameraNodeService.cs`: создаёт/обновляет узел камеры (`name`, `parent_id`), узел
   сервера, удаляет камеру целиком (узел, реплики, `camera_placement`, наблюдения) одной
   транзакцией; `EnsureCameraAsync(CameraSetup)`, `RemoveCameraAsync(camera_id)`,
   `ReconcileParentAsync(camera_id)`. `Clients/ICameraCatalog` расширяется `ListAsync(serverId?)` и
   `CameraInfo{Id, Name, ServerId}` (M2-конструктор `new CameraInfo(id,name)` обновить).
   `Adapter/CameraChangeSubscriber.cs` — подписка на `server-config-changed` и
   `camera-state-changed`: пустой `camera_ids` → `ListCameras(server_id)` и сверка сервера; иначе
   `GetCamera` по каждому id; `Found=false` → удаление. `Adapter/StartupResyncService.cs` —
   `ListMediaServers` + `ListCameras`, повтор до первого успеха (раз в 10 с). `Program.cs` —
   `DaprPublishSubscribeClient`, `DAPR_UTILS_APP_ID`, `DAPR_STATE_STORE_NAME`,
   `AddHostedService<...>`.
   Проверка: InMemory-тесты — узел без размещения под сервером, размещённая под пространством,
   перенос размещения не откатывается адаптером, удаление чистит реплики/наблюдения, повторный
   resync ничего не дублирует.

5. **`VmsMap` — адаптер: тревоги.**
   `Adapter/RetentionEventSubscriber.cs` (`retention-event-created`, политика `Retry`): payload
   `{camera_id, stream_name, alarm_start_ts}`, `FsService.ListRetentionEvents` по
   `DAPR_FS_APP_ID`/`fs_id` из `ListMediaServers`; запись `ALARM` + события одной транзакцией.
   `Adapter/AlarmSweepService.cs` — раз в 3 с выбирает камеры в `ALARM`, у которых
   `alarm_end_ts` в прошлом, перепроверяет эпизод и ставит `NORM`. `Adapter/CameraStateWriter.cs`
   — общий read-modify-write под advisory lock. `IStatesUpdateService.UpdateStates` перестаёт
   открывать транзакцию, если она уже открыта (как `RefreshAlarmsAsync`), чтобы адаптер мог
   писать состояние и событие атомарно.
   Проверка: InMemory-тесты — событие с будущим концом даёт `ALARM` и одно событие с id
   `event_uuid`; повторная доставка не дублирует; продление отодвигает конец; сверка снимает
   `ALARM` и ставит `NORM`; `CONNECTED`/`INFO` сохраняются. Postgres-тест — агрегация
   `children_alarms` у пространства и здания после тревоги камеры.

6. **`VmsMap` — адаптер: связь.**
   `Clients/IVmsUtilsClient.cs` + `VmsUtilsClient` (`GetMediaMtxPathsInfo` через
   `DaprClient.CreateInvocationInvoker(DAPR_UTILS_APP_ID)`); `Adapter/ConnectionStatePoller.cs`
   раз в 5 с собирает пути всех известных камер (кэш из resync/подписок), зовёт VmsUtils одним
   вызовом, пишет `CONNECTED`/`DISCONNECTED` при смене, сохраняя остальные состояния. Ошибка
   VmsUtils логируется, состояние не меняется.
   Проверка: InMemory-тест — `has_source=false` → `DISCONNECTED`, восстановление → `CONNECTED`,
   тревога не теряется; недоступность VmsUtils не плодит записей.

7. **`VmsMap` — `Watch`.**
   `Watch/MapWatchPoller.cs` (singleton): реестр подписок, объединённые боксы/id, чтение раз в
   секунду, дифф по `Version`, отправка в каналы подписок. `Watch/WatchSubscription.cs` —
   `WatchRequest` (`set_box`/`set_ids`/`update_values`, `Caller`), права на каждое сообщение.
   `MapServiceImpl.Watch` — bidirectional stream: читает запросы в одном цикле, пишет сообщения в
   другом; отмена/разрыв клиента снимает подписку. Фильтрация — `view`; для `set_ids2delete` —
   и узлы, вышедшие из бокса, и узлы, потерявшие право.
   Проверка: InMemory-тесты поллера — добавление/обновление/удаление узла, `set_visual_states`
   по `Version`, `on_values_changed` только при запросе, права отсекают чужой узел; сборка.

8. **web_srv — `api/map/*` и SSE.**
   `Controllers/MapObjectsController`, `MapStatesController`, `MapEventsController`,
   `MapTracksController`, `MapValuesController`, `MapDiagramsController`, `MapRightsController`,
   `MapLevelsController`, `MapFilesController`, `MapLiveController`. Все — `[Authorize(
   Authenticated)]`, редактирование — `AdminOnly`; на каждый запрос передаётся
   `CallerContext.From(User)` (username, роли, `unrestricted = !AuthGate.KeycloakEnabled`).
   `api/map/live` — SSE по образцу `AnalyticsLiveController`: один `Watch`-стрим на соединение,
   права перечитываются на каждое событие. `api/map/files/{id}` — чанки `GetFile`, `ETag`,
   `Content-Type`, 404. `web_srv.Server.csproj` уже включает `vms_map.proto`.
   Проверка: `dotnet build`; SSE отдаёт `set_visual_states` после тревоги; `api/map/files`
   отдаёт загруженный файл; фильтрация по правам видна в ответах.

9. **web_srv — `RightsFilter`/`RightsService` вместо `CameraAccess*`.**
   `Authorization/CameraAccessFilter.cs` → `RightsFilter.cs`, `CameraAccessService.cs` →
   `RightsService.cs`, `CameraAccessAttribute` → `RequiresRightAttribute`,
   `NoCameraAccessAttribute` → `NoRightsAttribute`, `CameraAction` → `RightActions` (строковые
   константы), `CameraScope` сохраняется как `RightsScope`. Механическая замена 94 атрибутов в
   контроллерах (`[CameraAccess(CameraAction.ViewLive, ...)]` →
   `[RequiresRight(RightActions.ViewLive, ...)]`), семантика флагов та же. `RightsService`:
   `GetEffectiveRights` из `VmsMap`, кэш 60 с + `rights-version` из state store (перенос
   `EnsureFreshAsync`/`Invalidate` из `CameraAccessService`), `IsAllowedAsync` по `camera_id`
   (узел камеры сопоставляется по `camera_id` из `NodeRights`), `ScopeAsync` — множество камер с
   действием, `Everything` в `open`/admin/unrestricted. `api/permissions*` — совместимый слой
   из решения выше; `PermissionManager` удаляется.
   Проверка: `dotnet build`; все 94 атрибута заменены (`grep -rn CameraAccess` пусто); сценарии
   live/архив/PTZ/экспорт под пользователем без прав дают те же 403, с правом на здание —
   работают; `api/permissions` читает и пишет права.

10. **VmsCfg — демонтаж прав.**
    Удалить `Grpc/PermissionDb.cs`, DbSet'ы/сущности прав из `VmsCfgDbContext`, четыре rpc
    (`ListCameraPermissions`, `SetUserCameraPermissions`, `GetCameraAclMode`, `SetCameraAclMode`)
    и сообщения из `proto/vms_cfg.proto` (без `reserved`), `BumpCameraPermissionsVersionAsync`,
    `CameraPermissionsVersionKey` и вызовы, `PermissionDb` из DI и конструктора `CfgServiceImpl`.
    Миграция `Grpc/migrations/0008_drop_permissions.sql`:
    `DROP TABLE IF EXISTS user_camera_permission, user_all_cameras_permission, camera_acl_setting;`
    Проверка: `dotnet build`; `dotnet test` (VmsCfg.Tests); на dev-БД после старта этих таблиц
    нет; на чистой БД они не появляются.

11. **Конфигурация и документация.**
    `.vscode/launch.json` — `DAPR_UTILS_APP_ID` и `DAPR_STATE_STORE_NAME` в «VmsMap (.NET)»;
    `.env`/`../ta_install/.env` уже содержат обе — проверить, не переписывать. `CLAUDE.md` —
    раздел «Login»: права теперь на узлах дерева в `VmsMap`, VmsCfg их не хранит; раздел
    «Maps & plans» — `GetEffectiveRights`, `rights-version`, адаптер и `Watch`; «Debug
    entity state» не трогаем. `docs/maps-and-plans.md` — владелец прав и поток тревог.
    `docs/future_plans/` — событие «поток пропал/появился» от `video_srv` вместо опроса
    VmsUtils (файл `camera-connection-state.md` уже заведён в README §7 — проверить и обновить).
    В этом файле — «Ход работы» после исполнения.
    Проверка: `grep -rn cam-perm-version` — только история планов; `grep -rn
    BumpCameraPermissionsVersion` пусто; `grep -rn "Vms\.Cfg\..*Permission"` пусто.

12. **Сквозная проверка.**
    `dotnet build dotnet/ta_vms.sln` — 0 ошибок, 0 предупреждений; `dotnet test` — все проекты
    (VmsMap, VmsCfg, VmsFs, VmsAnalyticsSingleton, VmsPtzTrackingSingleton) плюс
    `VMS_MAP_TEST_DSN` на `postgis/postgis:latest`. Dev-стек: все сервисы поднимаются, `VmsMap`
    мигрирует `ta_map`. Проверить сценарии ниже.

**Проверка M3:**

- **Тревога.** Через `FsService.RetainEvent` (grpcurl или одноразовый клиент) на `sim-master`:
  состояние `ALARM` появляется у узла камеры, `children_alarms` — у её пространства и здания в
  `GetVisualStates`; SSE `api/map/live` присылает `set_visual_states`; по окончании эпизода
  (`alarm_end_ts` в прошлом) сверка снимает `ALARM`, ставит `NORM`; событие `alarm` видно в
  `GetByFilter` с `param0`/`param1` и `alarm_end_ts`. Повторный `RetainEvent` с тем же
  `event_uuid` не даёт второго события.
- **Связь.** Остановка потока в OnvifSim (`PUT /api/cameras/{id}/network {"online":false}` или
  остановка публикации) даёт `DISCONNECTED` в течение ~5 с, восстановление — `CONNECTED`;
  `ALARM` при этом не теряется.
- **`camera-state-changed`** → свойство/состояние `armed`: включение охраны в web_srv меняет
  состояние узла камеры.
- **Права.** Пользователь без доступа не видит узел камеры, её состояния и события в
  `GetByParent`/`GetVisualStates`/`GetEventsByFilter`/`Watch`; право `view_live` на здании
  открывает live всех его камер; `admin` видит всё; `open`/`enforced` переключаются и сразу
  действуют на всех репликах (проверить `rights-version`).
- **Совместимость.** Все существующие проверки web_srv — live, архив, PTZ, экспорт — ведут себя
  как до переезда: те же 403/200 на тех же сценариях, что при введении `CameraAccess`; старая
  страница `api/permissions` читает и пишет матрицу.
- **Чтения и `Watch`.** `GetByParent` с пагинацией, `GetByIds`/`GetByBox` с фигурами, кластеры
  при >999, `GetByName`/`GetByParams`, события с поддеревом и keyset, треки, значения,
  мнемосхемы и типы, уровни, файлы. `Watch` по боксу и по id: добавление, перенос, удаление узла,
  смена состояния; права отсекают чужие узлы; при разрыве SSE подписка снимается.
- **Удаление камеры** в VmsCfg убирает её узел, реплики, размещения и наблюдения; `ta_map` не
  содержит сирот.
- **Сборка/тесты.** `dotnet build` 0/0; `dotnet test` зелёный; `VMS_MAP_TEST_DSN` — Postgres-тесты
  прав, `KMeans` и каскадов.
- **Стенд.** `tests/onvifsim/onvifsim_lab.py all` (или `up`/`provision`/`calibrate`/`aim-check`/
  `bind`/`run`) проходит как в M2; в `ta_map` у камер появляются узлы, состояния `CONNECTED`,
  события; наведение не меняется.

**Критерии готовности:** адаптер создаёт и удаляет узлы камер, переводит тревоги, `armed` и
связь в состояния и журнал; `MapService` отдаёт все чтения и `Watch`; права с наследованием
живут в `VmsMap`, `CameraAccess*` заменён на `Rights*`, VmsCfg не хранит права; `api/map/*` и
SSE работают; существующие проверки web_srv не изменились; `dotnet build`/`dotnet test` зелёные;
стенд OnvifSim проходит.

## Ход работы

Ветка `square-merge/m3-states-events-rights` в ta_vms. M2 перед ветвлением быстро перенесён в
`master` (`master` = `2bd5e15`). В `../ta_install` изменений нет.

| Коммит | Что |
|---|---|
| `be5b410` | этот план |

**Решения по ходу:**
- `GetByBox` принимает `Box` с `wn`/`es` как `Coordinate`, а не массивами Square: DTO-слой и так
  переводит углы, а `Geometry` для зон остаётся прежним.
- `GetDiagramTypes` — один rpc на id, имена и подстрочный фильтр: у Square это были три метода,
  но вызывающему всегда нужен ровно один из трёх ключей.
- `GetLevel` отделён от `GetLevels`: редактору нужен один уровень по имени, а карте — все 190.

**Проверено:**
- `dotnet build dotnet/ta_vms.sln` — 0 ошибок, 0 предупреждений (30 с).

