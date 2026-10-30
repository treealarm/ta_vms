# M1 — каркас `VmsMap`: ядро Square, PostGIS, запись (детальный план)

> Часть плана [Square → ta_vms](README.md); ссылки «§N» ведут в его разделы.
> Файлы — ориентиры, не полный перечень. Шаги отмечаются ✅ по мере исполнения; «Ход работы» —
> в конце.

M1 — первый майлстоун переноса: появляется `dotnet/VmsMap`, единственный владелец базы `ta_map`
и, по образцу VmsCfg, источник истины о дереве объектов, геометрии, состояниях и событиях;
остальные сервисы ходят к нему по gRPC и никогда в его базу. В него модульно переезжает
серверное ядро Square (`Domain`, `DbLayer`, `DataChangeLayer` — 7,5 тыс. строк C#, минус всё
интеграционное), создаётся схема с PostGIS, работает **запись** по gRPC. Агрегация тревог
(`BlinkService` на Dapr Actors) заменяется пересчётом `alarm_states` в транзакции записи
состояний и покрывается тестами.

**Границы.** Не входят: чтение как контракт, `Watch`/SSE, адаптер (§4.4), пространства и
спатиальный модуль (M2), права с наследованием и фильтрация (M3), фронтенд (M4), переписывание
английской документации (M5). Схема создаётся вся, кроме таблиц M2 (`spaces`, `space_plan`,
`camera_placement`, `landmark`, `ptz_geo_observation`, `ground_observation`,
`geo_objects.canvas_id`).

## Решения

- **Один проект** `dotnet/VmsMap` (`Microsoft.NET.Sdk.Web`, net10.0) с папками `Domain/`, `Db/`,
  `Grpc/`; `Adapter/` не создаётся до M3. Пространства имён `VmsMap.*`; proto-пакет `vms.map`
  даёт CLR-пространство `Vms.Map`.
- **Ядро берётся целиком**: `Domain` + `DbLayer` сливаются в один проект; методы чтения
  перенесённых классов сохраняются (их потребители — M2/M3). Не переносятся `IntegroService`,
  `ActionsService`, `GroupService` и их DTO/модели/таблицы, `PubSubLib`, `IntegrationHost`,
  `IntegrationUtilsLib`, `GrpcDaprLib`, `Protos/*` (только как образец форм), `ValhallaLib`,
  `KMeans`, `FileSystemService`.
- **Рефакторинг §3.1 — сразу** (шаг 6).
- **`rights` — сразу в целевой форме** §4.7: `(object_id, subject, action)`; наследование в коде
  — M3. Данных нет, вторая миграция не нужна.
- **`visual` — в `0001`**: типизированная замена магическим свойствам Square (`__color`,
  `__image*`, `__image_rotate`, `__text`); `properties` остаются абстрактными свойствами.
- **Файлы — в БД** (§4.6): `files`; `visual.image` — `bytea` для иконок; фоны мнемосхем и
  картинки событий — `files`. Строки-пути на том `leaflet_data` не переживают перенос.
- **PostGIS**: `postgis/postgis:latest` в трёх compose; на Windows — zip-бандл PostGIS 3.6.2 для
  PG 18 в `versions.json`, распаковка в `pgsql` на этапе `Build-Stage.ps1`; `CREATE EXTENSION` —
  в миграции.
- **`DbMigrator` → `dotnet/VmsDb`**: четвёртая копия — повод (README §4.1). Поведение каждого
  сервиса сохраняется: свой advisory lock, свои сиды.
- **Статические кэши не переносятся как статические** (уровни, тревожные состояния): экземплярный
  кэш с инвалидацией по записи — иначе реплика `VmsMap` и админка расходятся.
- **Ветка** `square-merge/m1-vmsmap-core` в ta_vms (та же в ta_install), от `master`; коммиты —
  по готовым кускам.

## Шаги

1. ✅ **`dotnet/VmsDb` — общий мигратор (предпосылка, отдельный коммит).**
   Новый classlib (net10.0, `Npgsql` 10.0.3), без ASP.NET и EF. API `VmsDbMigrator.MigrateAsync`:
   подключение с ретраями и `InvalidCatalogName` → сообщение с именем сервиса и БД;
   `schema_migrations(name, applied_at)`; прогон `<contentRoot>/Grpc/migrations/*.sql` (fallback —
   `AppContext.BaseDirectory`) в лексикографическом порядке, каждая миграция в своей транзакции;
   session-level advisory lock по настраиваемому ключу; необязательный хук после миграций
   (`Func<NpgsqlConnection, CancellationToken, Task>`). Сиды VmsCfg (MediaMTX по умолчанию,
   `NodeRegistry.SeedConfiguredAsync`, default storage) остаются в VmsCfg и передаются хуком.
   Перевести `dotnet/VmsCfg/Grpc/DbMigrator.cs`, `dotnet/VmsFs/Grpc/DbMigrator.cs`,
   `dotnet/VmsAnalyticsSingleton/Grpc/DbMigrator.cs`; ключи блокировок сохранить
   (`hashtext('ta_vms_schema_migrations')`, 7743512879, 7743512880; для VmsMap — 7743512881).
   Файлы миграций и `<Content CopyToOutputDirectory>` не трогаются.
   Проверка: сборка, `dotnet test` (VmsCfg 26, VmsFs 5, VmsAnalyticsSingleton 24), dev-стек
   мигрирует три БД как раньше.

2. ✅ **Каркас `dotnet/VmsMap`.**
   `VmsMap.csproj`: пакеты как у VmsCfg (Dapr 1.18.7, Google.Protobuf 3.35.1,
   Grpc.AspNetCore/Tools 2.83.0, Npgsql + EF provider 10.0.3) плюс
   `Npgsql.EntityFrameworkCore.PostgreSQL.NetTopologySuite` 10.0.3; `ProjectReference` на `VmsDb`
   и `VmsLog`; `Protobuf Include="..\..\proto\vms_map.proto" GrpcServices="Both"`;
   `Content Include="Grpc/migrations/*.sql" CopyToOutputDirectory="PreserveNewest"
   CopyToPublishDirectory="PreserveNewest"`.
   `Program.cs`: `AddVmsLogging()` (требует `APP_ID`), `RequirePort("MAP_VMS_PORT")`, `AddGrpc()`,
   `AddDaprClient()` (публикаций и подписок в M1 нет); `NpgsqlDataSource` — **singleton**, один
   на процесс, с `EnableDynamicJson()` и `UseNetTopologySuite()` (в Square он строился на каждый
   DI-scope); `AddDbContext<VmsMapDbContext>` (scoped, `UseNetTopologySuite()`, без
   `EnableRetryOnFailure(int.MaxValue)`) — портированные сервисы принимают `PgDbContext` в
   конструктор, как в Square, поэтому регистрация scoped, а gRPC-реализация не singleton;
   `AddSingleton<VmsDbMigrator>` + `MigrateAsync()` до `MapGrpcService`. DSN по контракту §4.1:
   `VMS_MAP_POSTGRES_DSN` → `POSTGRES_DSN` → `POSTGRES_HOST/PORT/USER/PASSWORD` +
   `POSTGRES_MAP_DB`, каждое через `Require`. Kestrel — `ListenLocalhost(port, Http2)`.
   `Dockerfile` по образцу `dotnet/VmsCfg/Dockerfile` (контекст — корень репозитория,
   `COPY proto`); проект — в `dotnet/ta_vms.sln`.

3. ✅ **Перенос `Domain`.**
   Из `Domain/` Square (92 файла, ≈1,9 тыс. строк): дерево (`BaseObjectDTO`, `BaseMarkerDTO`,
   `MarkerDTO`, `ObjPropsDTO`, `ObjExtraPropertyDTO`, `KeyValueDTO`, `GetByParentDTO`,
   `GetBySearchDTO`), состояния и тревоги (`ObjectStateDTO`, `ObjectStateDescriptionDTO`,
   `AlarmState`/`AlarmBaseState`, `AlarmObject`, `MarkersVisualStatesDTO`), события (`EventDTO`,
   `SearchEventFilterDTO`, `SearchFilterDTO`, `SortDTO`), треки (`TrackPointDTO`, `BoxTrackDTO`),
   геометрия (`Geo2DCoordDTO`, `GeometryDTO`, `GeometryCircleDTO`, `GeometryPolygonDTO`,
   `GeometryPolylineDTO`, `GeoObjectDTO`, `BoxDTO`, `FigureGeoDTO`, `FigureZoomedDTO`,
   `FiguresDTO`, `ZoomComparer`), мнемосхемы (`DiagramDTO`, `DiagramCoordDTO`, `DiagramTypeDTO`,
   `DiagramTypeRegionDTO` и обёртки), значения (`ValueDTO`), права (`ObjectRightValueDTO` →
   `RightDTO{object_id, subject, action}`), уровни (`LevelDTO`), `VisualTypes`, `PropertyCopy`,
   `Utils`, `ObjectInterfaces`, `ServiceInterfaces` (без интеграционных, публикационных и
   `IRequestContextProvider`).
   Не переносятся: `Integro/`, `Actions/`, `Groups/`, `PubSubTopics`, `StateWebSock/*`,
   `BackendLog`, `CustomerLoginDto`, `MapDatabaseSettings`, `RoutingSettings`, `EnvConfig`
   (заменяется `Require`).
   `IntegrationServerLib/ObjectIdGenerator` → `VmsMap/Domain/` (детерминированный md5-uuid из
   `camera_id` + версии — так адаптер M3 выводит id узла камеры); `GeometryProtoConvert` →
   `VmsMap/Grpc/`, с исправлением: `LineString` не превращается в `GeometryPolygonDTO`.

4. ✅ **Перенос `Db/`.**
   `PgDbContext` (530 строк, 23 `DbSet`) + `Models/` → `VmsMap/Db/`; Npgsql + NetTopologySuite;
   `IRequestContextProvider` и `DbSet` интеграций/actions/groups удаляются; классы остаются
   `internal` — после слияния в одну сборку `InternalsVisibleTo` не нужен. `[Timestamp] uint
   Version` (xmin) сохраняется — на нём будет построен `Watch` в M3.
   `pg_services/*` → `VmsMap/Db/`: `MapService` (переименовать в `MapDb`, чтобы не путать с gRPC
   `MapService`), `StateService`, `EventsService`, `GeoService`, `TrackService`,
   `DiagramService`, `DiagramTypeService`, `ValuesService` (вернуть в namespace из глобального),
   `LevelService`, `RightService` (переписать под целевую форму прав), `ModelGate`,
   `QueryExtension`.
   Из `DataChangeLayer` (12 файлов, 573 строки) сливаются с сервисами `Db/`: `MapUpdateService`
   (оркестрация `UpdateProperties`/`UpdateFigures`/`Delete`), `TracksUpdateService`,
   `DiagramUpdateService`, `DiagramTypeUpdateService`, `ValuesUpdateService`,
   `RightUpdateService`, `EventsUpdateService`, `StatesUpdateService` (отдельный класс — место
   агрегации тревог). Не переносятся `IntegroUpdateService`, `GroupUpdateService`,
   `ActionsUpdateService`. `ServicesConfigurator` заменяется регистрацией в `Program.cs`.
   Новые: `VisualService` и `FileService` (`files`).
   Сиды `InitHostedService` (`LevelService.RebuildCache` — 190 уровней, `StateService.Init` —
   5 состояний) переезжают в миграцию; в коде остаётся чтение и запись.
   `KMeans` **не переносится в M1**: единственный потребитель — `GetByBox` при >999 фигурах
   (M3); переносится вместе с ним (YAGNI).

5. ✅ **`proto/vms_map.proto` — только запись.**
   `package vms.map`, стиль `vms_cfg.proto` (`google.protobuf.Empty/Timestamp`,
   `OperationStatus`, `XxxRequest/Response`, плоские импорты). Формы сообщений — из `tracks.proto`
   Square (`ProtoObject`, `ProtoObjProps`, `ProtoFig`, `ProtoObjectState`, `EventProto`,
   `TrackPointProto`, `ValueProto`, `DiagramProto`), без префикса `Proto`, с `rotation` в
   координатах мнемосхем. `service MapService`:
   - объекты: `UpdateObjects`, `UpdateProperties`, `UpdateFigures`, `DeleteObjects`;
   - состояния: `UpdateStates`, `UpdateStateDescriptions`;
   - события: `UpdateEvents`;
   - значения: `UpdateValues`, `DeleteValues`;
   - треки: `UpdateTracks`;
   - мнемосхемы: `UpdateDiagrams`, `DeleteDiagrams`, `UpdateDiagramTypes`, `DeleteDiagramTypes`;
   - права: `UpdateRights`, `DeleteRights`;
   - файлы: `PutFile` (client-streaming), `GetFile` (server-streaming), `GetFileInfo`.
   Уровни в M1 только читаются (сид в миграции); rpc их записи — с редактором в M4 (YAGNI).
   Чтение объектов, состояний, событий и `Watch` — M2/M3; в M1 этих rpc нет.

6. ✅ **Рефакторинг при переносе (§3.1) — обязательный минимум M1.**
   - `UpdateHierarchyAsync`: для существующего объекта вызывается `_db.Markers.Update(existing)`
     без копирования новых значений — переименование и перенос теряются; копировать поля поверх
     существующей сущности. Тест.
   - `RemoveAsync`: свойства удаляются по `id` вместо `object_id`, каскада нет. Удалять поддерево
     (рекурсивный CTE) и всё боковое одной транзакцией; пересчитать тревоги у затронутых предков
     (шаг 7).
   - `GetOwnersAndViewsAsync`: `views.Union(ownersDb)` теряет обычные объекты из входа —
     объединять с `owners` из `listObjects`. Тест.
   - `events`/`event_props`: PK, keyset-пагинация (сортировка в БД, `(timestamp, id)`), фильтр по
     поддереву (рекурсивный CTE) вместо `groups`, убрать `ToQueryString()` в консоль.
   - `GetAllChildren`, `GetByChildIdAsync`, `GetByParentIdsAsync`: рекурсивный CTE вместо запроса
     на узел/уровень и `Guid.CompareTo` внутри EF-запроса.
   - `StatesUpdateService`: статический `_alarmDescr` не инвалидируется — экземплярный кэш с
     инвалидацией после `UpdateStateDescriptions`.
   - `LevelService._cache`: статический, не инвалидируется — экземплярный (сейчас загрузка при
     первом обращении; инвалидация — с записью уровней в M4).
   - `StateService.UpdateStateDescrsAsync`: `Guid.Empty` при пустом `id` → коллизия PK;
     генерировать id.
   - `EventsService`: сравнение `visual_type` с `"Double"`/`"DateTime"` вместо `VisualTypes`
     (`__double`, `__datetime`).
   - `DiagramTypeService`: не глотать все исключения молча; `DiagramService.ConvertListDB2DTO` не
     возвращает `null` вместо пустого списка.
   - `diagrams.dgr_type` — FK на `diagram_types.id`; `rotation` в координатах.
   - Идентификаторы объектов — строки UUID (36 символов); 24-символьные id из `KMeans`/REST
     Square не переносятся.

7. ✅ **Агрегация тревог.**
   В `StatesUpdateService.UpdateStates`, одной транзакцией: отфильтровать реплики
   (`GetOwnersAsync` — состояние реплики не пишется); `pg_advisory_xact_lock(hashtext(
   'ta_map_alarm'))` — сериализует пересчёт между репликами; записать
   `object_states`/`object_state_values`; собрать затронутые объекты (изменённые, их предки
   рекурсивным CTE, при удалении — предки удаляемого, при переносе — цепочки старого и нового
   родителя); **пересчитать от таблиц**, не инкрементировать счётчик: `alarm` объекта — любое его
   состояние из справочника с `alarm=true`; `children_alarms` — число прямых детей, у которых
   `alarm` или `children_alarms > 0`; писать только изменения.
   Так исчезают рассинхроны `BlinkService`: счётчик, не записываемый при `children_alarms 1→2`,
   протухший `ParentId` в акторе, счётчики, потерянные при рестарте, незачищенные тревоги
   удалённых объектов, кэш справочника. Публикации `AlarmStatesChanged` нет: живое обновление —
   `Watch` в M3. Логика пересчёта — чистая функция (вход: `objects(id, parent_id)`,
   `alarm_states`, набор тревожных состояний; выход: изменённые строки), тестируется без БД.

8. ✅ **`Grpc/MapServiceImpl`.**
   Тонкий слой: proto ↔ DTO, вызов сервисов `Db/`. Шаги, которые Square делал несколькими
   `SaveChanges` без общей транзакции (`UpdateProperties`: иерархия → свойства; `Delete`: geo →
   marker → diagram; `UpdateFigures`: иерархия → свойства → geo → трек), — одной транзакцией.
   Ошибки — `RpcException` (`INVALID_ARGUMENT`, `NOT_FOUND`). `PutFile`/`GetFile` — чанки по
   1 MiB (в пределах дефолтных 4 MiB); лимит файла 20 MiB проверяет сервис.

9. ✅ **Миграция `Grpc/migrations/0001_square_schema.sql`.**
   Единственный источник — `multitenant_admin/mt_admin/init_files/initdb/sql_scripts/*.sql`
   (15 файлов, 23 таблицы; в Square SQL нет). Пишется **один** файл в стиле ta_vms:
   - `CREATE EXTENSION IF NOT EXISTS postgis;` и `pg_trgm` (для `gin_trgm_ops`);
   - остаются 16 таблиц: `objects`, `properties`, `rights`, `levels`, `geo_objects`, `diagrams`,
     `diagram_types`, `diagram_type_regions`, `events`, `event_props`, `track_points`,
     `db_values`, `object_states`, `object_state_values`, `object_state_descriptions`,
     `alarm_states`; не создаются `groups`, `integro`, `integro_types`,
     `integro_type_children`, `action_executions`, `action_parameters`, `action_result`
     (`map_cash` — каталог на томе, не таблица);
   - новые `visual` (`object_id` PK/FK → `objects` ON DELETE CASCADE, `color`, `image` bytea,
     `image_width`, `image_height`, `rotation_deg`, `text`) и `files` (`id`, `mime`, `bytes`,
     `etag`); `diagrams.background_img text` → `file_id uuid REFERENCES files(id)`,
     `diagram_types.src text` → `file_id`;
   - `rights` целевой формы: `(object_id uuid, subject text, action text, PRIMARY KEY
     (object_id, subject, action))`, FK → `objects` ON DELETE CASCADE;
   - `events`: `id uuid NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY`; `event_props.id` — PK,
     `owner_id uuid REFERENCES events(id) ON DELETE CASCADE`; keyset-индексы `(timestamp DESC,
     id DESC)` и `(object_id, timestamp DESC, id DESC)`; индексы Square (param0/1, trgm)
     сохраняются;
   - `diagrams.dgr_type text` → `uuid REFERENCES diagram_types(id)`; `diagrams.id` и
     `geo_objects.id` — PK и FK → `objects(id) ON DELETE CASCADE` (это id объекта);
   - каскад удаления (§3.1): FK `ON DELETE CASCADE` на `objects(id)` у `properties`, `rights`,
     `visual`, `geo_objects`, `diagrams`, `object_states`, `alarm_states`, `events`,
     `track_points`, `db_values`; `objects.parent_id` — **без** self-FK (поддерево удаляет
     `DeleteObjects`);
   - `alarm_states.alarm` — `boolean NOT NULL DEFAULT false`;
   - сиды: 5 состояний (`CONNECTED`, `DISCONNECTED`, `ALARM`, `INFO`, `NORM` с цветами и `alarm`)
     и 190 уровней (`j = 1..19`, `i = j..19`, имена `"j"`/`"j-i"`) — `INSERT ... SELECT
     generate_series(...)` с `gen_random_uuid()`.

10. ✅ **PostGIS в развёртывании.**
    `docker-compose.yml` (dev): `postgres` и `db-init` → `postgis/postgis:latest` (данные в
    `./data/postgres` переживают смену образа, extension создаёт миграция);
    `../ta_install/docker-compose.yml` и `docker-compose.distributed.yml` — то же.
    `../ta_install/windows/versions.json`: компонент `postgis` (3.6.2 для pg18, zip с
    `postgresql.org/ftp/postgis/pg18/v3.6.2/win64`, URL + sha256); `scripts/Build-Stage.ps1` —
    распаковка `bin/`, `lib/`, `share/extension/` (и зависимых DLL) в `pgsql`; лицензия — в
    `THIRD-PARTY-NOTICES`. `TaVmsHost` не меняется: `POSTGRES_MAP_DB` попадает в набор создаваемых
    БД по общему правилу, `CREATE EXTENSION` делает миграция.

11. ✅ **Тесты `dotnet/VmsMap.Tests`.**
    xunit 2.9.3 + `Microsoft.NET.Test.Sdk` 17.14.1 + coverlet 6.0.4 +
    `xunit.runner.visualstudio` 3.1.4 (как у VmsCfg.Tests), `ProjectReference` на `VmsMap`; без
    Testcontainers, EF InMemory — как в `VmsAnalyticsSingleton.Tests`.
    Без БД: `UpdateHierarchyAsync` (новые значения доходят), `GetOwnersAndViewsAsync` (обычные
    объекты не теряются), чистая функция агрегации (лист → все предки; два ребёнка → 2; снятие
    одного; повтор идемпотентен; удаление тревожного ребёнка снимает счётчик; перенос между
    родителями), маппинг `visual`, `FileService` round-trip.
    С настоящим Postgres — отдельный класс, включаемый `VMS_MAP_TEST_DSN` (иначе skip с явным
    сообщением): рекурсивный CTE и транзакция целиком, включая каскадное удаление. Запускается на
    dev-стеке и входит в проверку M1 (в CI `windows.yml` Postgres нет).

12. ✅ **Чек-лист нового сервиса.**
    ta_vms: `.env` (`POSTGRES_MAP_DB=ta_map`, `MAP_VMS_PORT=5151`, `DAPR_MAP_APP_ID="vmsmap"`);
    `docker-compose.yml` (`vmsmap` в профиле `app` + `vmsmap-dapr`, loopback, `network_mode:
    service:vmsmap`); `dotnet/dapr.yaml` (appPort 5151, daprHTTP 3617, daprGRPC 51018, metrics
    9207, internalGRPC 19028); `.vscode/launch.json` (конфигурация `VmsMap (.NET)` и оба компаунда
    `Full stack (F5)`); `.vscode/tasks.json` (`prepare:vmsmap`, `dapr:vmsmap:start/stop`);
    `dotnet/ta_vms.sln`; `dotnet/VmsMap/Dockerfile`; `.github/workflows/images.yml` (строка
    матрицы `vmsmap`; `windows.yml` подхватывает `dotnet/*/Dockerfile` сам); `CLAUDE.md` — строка
    `VmsMap` в «Scaling model per service» (реплицируемый, без суффикса Singleton) и правка
    чек-листа: `dotnet/.vscode/launch.json` и `video_srv/.vscode/launch.json` не существуют.
    `../ta_install`: `.env`; `docker-compose.yml` (3617 / 41018 / 19028 / 9207);
    `docker-compose.distributed.yml` (секция core); `scripts/push-images.sh`
    (`ta_vms-vmsmap` → `treealarm/vmsmap`); `windows/config/services.json`
    (`roles: standalone, core`, exe `dotnet\VmsMap\VmsMap.exe`); `windows.env` — только при
    отличиях (5151 < 49152, не нужно); `scripts/distributed.sh` — правок не требует.
    `multitenant_admin`: правок нет — `ta_map` создаётся из `POSTGRES_*_DB` (`db-init`/
    `TaVmsHost`), Square-скрипты остаются до M5; проверку записать в «Ход работы».

13. ✅ **Документация.**
    Этот файл — с чекбоксами ✅ по мере исполнения; «Ход работы» — в конце. `CLAUDE.md` — только
    правки шага 12; описание модели и владельца карты — M2/M5.

**Проверка:** `dotnet build dotnet/ta_vms.sln` (0 ошибок, 0 предупреждений) и `dotnet test` — все
проекты, включая `VmsMap.Tests` (БД-тест — с `VMS_MAP_TEST_DSN`); `docker compose config` (с
`--profile app` и без) и оба compose в `../ta_install` валидны; чистая БД: `postgres` + `db-init`,
`VmsMap` стартует, миграция проходит, `SELECT postgis_version();` непустой, `\dt` — 18 таблиц,
`SELECT count(*) FROM levels` = 190; рестарт не применяет миграцию повторно; `grpcurl` через
Dapr-сайдкар по `proto/vms_map.proto` (метаданные `dapr-app-id: vmsmap`) — `UpdateObjects`,
`UpdateProperties`, `UpdateFigures`, `UpdateStates`, `UpdateEvents`, `UpdateValues`,
`UpdateTracks`, `UpdateDiagrams`, `UpdateRights`, `PutFile` пишут строки в `ta_map`; тревога у
листа даёт `children_alarms` у всех предков, снятие — обратно, два ребёнка → 2, повтор
идемпотентен, удаление тревожного листа снимает счётчик; `grep -ri square dotnet/VmsMap` — только
комментарии о происхождении кода.

**Критерии готовности:** `VmsMap` есть во всех местах чек-листа (ta_vms и ta_install) и стартует
из compose и из Windows-манифеста; схема `ta_map` создаётся миграцией `0001` с PostGIS и
повторный старт её не трогает; запись объектов, состояний, событий, значений, треков, мнемосхем,
прав и файлов работает по gRPC; агрегация тревог покрыта тестами (лист → предки, снятие,
несколько детей, идемпотентность, удаление/перенос); `dotnet build` и `dotnet test` зелёные;
PostGIS в трёх compose и в `windows/versions.json`.

## Ход работы

Ветка `square-merge/m1-vmsmap-core` в ta_vms и та же в ta_install, от `master` `ca134f7`.
Сделано 2026-10-30, по коммиту на кусок:

| Коммит | Что |
|---|---|
| `bb7e982` | этот план |
| `e4a3cbb` | `dotnet/VmsDb`, три сервиса переведены на него (шаг 1) |
| `cb0e4b7` | `proto/vms_map.proto` — запись |
| `447fc72` | каркас `VmsMap` и перенос `Domain` |
| `ea57e94` | `Db/`, миграция `0001`, gRPC-запись, агрегация тревог |
| `2d58d77` | тесты `VmsMap.Tests` и три дефекта, найденных живым запуском |
| (этот) | PostGIS в compose и на Windows, чек-лист, `CLAUDE.md` |

**Решения по ходу, которых не было в плане:**
- `vms_map.proto` получил `UpdateVisual`: без него `visual` нечем было бы записать, а магические
  свойства Square не переносятся.
- Пересчёт тревог при переносе и удалении узла делает `MapUpdateService` (он и так оркестратор
  записи), а не gRPC-слой: иначе прямой вызов сервисов оставлял бы счётчики предков устаревшими.
- Чистые обёртки `DataChangeLayer` (`EventsUpdateService`, `RightUpdateService`,
  `DiagramUpdateService`, `DiagramTypeUpdateService`, `ValuesUpdateService`) не перенесены как
  классы: gRPC зовёт сами сервисы `Db/`, интерфейсы-обёртки удалены.
- `VmsMap` собран с `<Nullable>annotations</Nullable>`: у портированного кода Square анализ
  потока значений не чист, а включать `enable` ценой сотни предупреждений в M1 незачем.
- `db_values.value` — JSON-текст, а не `JsonDocument`: так модель грузится и под InMemory, а
  колонка всё равно `jsonb` (Npgsql отображает `string` в jsonb по типу хранилища).
  `ValuesService.UpdateListAsync` стал настоящим upsert: Square помечал новый value как
  Modified, и вставка падала concurrency-ошибкой на несуществующей строке.
- Фильтр событий: `groups` из DTO убран, поддерево — по `objects`; пагинация — keyset по
  `(timestamp, id)` с курсором-`start_id`; сортировка только по `timestamp`, прочие ключи —
  ошибка, а не тихое игнорирование (M3 доведёт контракт).
- Чек-лист `CLAUDE.md` про `dotnet/.vscode/launch.json` и `video_srv/.vscode/launch.json` править
  не пришлось: в текущей редакции этих путей нет, есть только корневой `.vscode/launch.json`.

Отклонения от прежней редакции этого файла (сознательные):
- `KMeans` не переносится в M1 — единственный потребитель `GetByBox` (M3).
- rpc записи уровней нет: уровни сеются миграцией, редактор — M4.
- `files` и `visual` создаются сразу в `0001`; права — сразу в целевой форме
  `(object_id, subject, action)`.
- PostGIS на Windows раскладывается `Build-Stage.ps1`, а не `TaVmsHost`.

**Проверено 2026-10-30:**
- `dotnet build dotnet/ta_vms.sln` — 0 ошибок, 0 предупреждений; `dotnet test` — все проекты:
  VmsMap 19, VmsCfg 26, VmsFs 5, VmsAnalyticsSingleton 24, VmsPtzTrackingSingleton 96.
  Пять тестов `VmsMap.Tests` идут только с `VMS_MAP_TEST_DSN`; прогон с ним на
  `postgis/postgis:latest` (PostgreSQL 17.5, PostGIS 3.5) — зелёный.
- Миграция на чистой БД: `schema_migrations` = 1, `postgis_version()` непустой, 18 таблиц,
  `levels` = 190, пять состояний на месте. Повторный старт миграцию не применяет.
- `grpcurl` по `proto/vms_map.proto` в запущенный сервис: `UpdateObjects`, `UpdateStates`,
  `UpdateRights`, `UpdateEvents`, `UpdateValues`, `UpdateTracks`, `UpdateDiagramTypes`,
  `UpdateDiagrams`, `PutFile` (два чанка), `DeleteObjects`. Тревога листа дала
  `children_alarms` у этажа и здания, `NORM` снял, повтор идемпотентен; удаление камеры
  убрало её события, права и значения и обнулило счётчик этажа; поворот региона (15°) и
  диаграммы (90°) сохранён.
- `docker compose config` (с `--profile app` и без) и оба compose в ta_install — валидны.
  Dev-БД на `postgis/postgis:latest` поднялась на прежнем каталоге данных (PG 17.5), `db-init`
  создал `ta_map`.

**Не проверено:**
- Полный dev-стек (F5) и профиль `app` с собранными образами, включая сайдкар `vmsmap`.
- Windows: `Build-Stage.ps1` с новым компонентом `postgis` (скрипт не запускался), установка
  PostgreSQL 18 + PostGIS, старт `VmsMap.exe` под `TaVmsHost`.
- `../ta_install/scripts/push-images.sh` для `vmsmap` — образ не собирался и не публиковался.
- Чтение (M2/M3) в новом коде не проверялось: rpc чтения в M1 нет, а портированные методы
  чтения проверены только там, где их трогает запись.
