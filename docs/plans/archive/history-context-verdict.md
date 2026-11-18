# Square → ta_vms: контекст, вердикт и что берём из Square

> История решения (2026-10-28), вынесенная из `../square-merge/README.md`, чтобы план не
> читался целиком ради того, что уже сделано. Всё, что здесь решено, уже в коде и описано
> по-английски в `docs/maps-and-plans.md`, `docs/maps/states-and-alarms.md` и
> `docs/maps/control.md`.

## 1. Контекст

Square (`../Square`) — платформа мониторинга объектов: дерево объектов, карта (Leaflet), состояния
с тревогами и подсветкой родителя, журнал событий, живые значения, треки, мнемосхемы, интеграции по
gRPC с самоописываемыми командами. ta_vms встраивается в него как видеоподсистема
(`docs/square-integration-plan.md`), но интеграция полуготова: экспорт пространств «не проверен на
живом Square», команды из Square не подключены, dev-compose ta_vms не поднимается без чужой
docker-сети, а у оператора две карты и два интерфейса.

Решение пользователя (2026-10-28): Square как самостоятельный проект исчерпан; его платформа
(серверная модель и протокол) ценна как **слой карты и событий внутри ta_vms**. Фронтенд Square
(17 тыс. строк, писался вручную, без тестов) модульно не берётся — только как образец.

### Что показала разведка

| | Square | ta_vms |
|---|---|---|
| Сервер | ~13,5 тыс. строк C# (.NET 10, EF Core + Npgsql + NetTopologySuite, Dapr). Ядро `Domain`/`DbLayer`/`DataChangeLayer`/`PubSubLib` ≈ 7,5 тыс. строк, за интерфейсами `I*Service`, со своими DI-конфигураторами. Единственная внешняя связка — имя БД из `DB_REALM_NAME` и необязательный `IRequestContextProvider`, результат которого не используется | web_srv ~11 тыс. строк C#; спатиальный модуль VmsCfg (`SpatialDb.cs` 735 + `CfgServiceImpl.Spatial.cs` 332 строки); `VmsSquareSingleton` ~1,3 тыс. строк |
| Схема БД | 26 таблиц, 362 строки SQL в 15 файлах — в **чужом** репозитории `multitenant_admin/mt_admin/init_files/initdb/sql_scripts/`. Миграций нет. PostGIS (`geometry(GEOMETRY,4326)` в `geo_objects`, `track_points`) | обычный `postgres` везде: dev compose, `../ta_install` (оба compose), Windows-инсталлятор пинит бинарники PostgreSQL 18.6 |
| Тесты | 0 | есть (VmsCfg, VmsFs, Analytics, PtzTracking, SquareSingleton) |
| Живые обновления | сырой WebSocket `/state` без аутентификации; каждое соединение поллит БД раз в 1 с по `xmin`; события в UI — поллинг раз в 3 с | web_srv без подписок: поллинг `api/fs/alarms` раз в 3 с, SSE `api/analytics/live` поверх gRPC-стрима |
| Тревога вверх по дереву | `BlinkService` на Dapr Actors: счётчик `children_alarms` в `alarm_states`; известные рассинхроны (пишется только при смене флага, `ParentId` кэшируется в акторе, статический кэш тревожных состояний не инвалидируется) | подсветка вложенных пространств считается в браузере из списка тревог |
| Фронтенд | React 18, MUI 7, RTK, Leaflet; 17 тыс. строк; 99 `any`; `App.jsx` | React 19, MUI 7, RTK, i18next, Leaflet + react-leaflet 5; 35 тыс. строк, из них карта 3,7 тыс. |
| Известные дефекты | `MapService.UpdateHierarchyAsync` не копирует новые значения (переименование/перенос теряется); `MapService.RemoveAsync` удаляет свойства по своему `id`, а не по `object_id`; EF-ключ `integro_types` — `i_type` вместо `(i_type, i_name)`; у `events` нет PK; `ExecuteActions` не проверяет право Control; права проверяются только в `GetByBox` | — |

PostGIS под Windows для PostgreSQL 18 есть: bundle 3.6.2 (март 2026), в том числе zip с
бинарниками, которые копируются в каталог PostgreSQL и включаются `CREATE EXTENSION`
(`postgis.net/documentation/getting_started/install_windows/released_versions/`,
`postgresql.org/ftp/postgis/pg18/v3.6.2/win64`).

## 2. Вердикт

**Объединять — да.** Серверное ядро Square берётся **модульно** (проекты как есть, с рефакторингом
под правила ta_vms), а не переписывается: это работающие годами 7,5 тыс. строк, переписывать их ради
стиля — против DRY/KISS. Кусками переносится только то, что в ta_vms устроено иначе:
`LeafletAlarms` (контроллеры ходят в БД напрямую, а в ta_vms базой владеет один сервис, web_srv
говорит с ним по gRPC), `StateWebSocket` (становится gRPC-стримом из сервиса карты и SSE в web_srv)
и `BlinkService` (логика берётся, акторы заменяются на обновление в той же транзакции).

**PostGIS остаётся.** Для слоёв масштаба, треков и запросов «что в области/зоне» нужен
пространственный индекс. Цена — образ `postgis/postgis` вместо `postgres` в трёх compose-файлах и
ещё один пинованный артефакт в `../ta_install/windows/versions.json`.

**Одно дерево.** Таблица `objects(id, parent_id, owner_id, name)` Square — единственное дерево.
Пространство ta_vms (space) — вид узла с боковой таблицей, как `diagrams` у Square. Камера после
размещения — объект дерева с геометрией и свойствами; что это камера, знает боковая таблица
размещения (`camera_placement` с `camera_id`).

**Две модели картинки, у каждой свой смысл.** План ta_vms — всегда «вид сверху» в WGS84: у
геометрии есть физический смысл (калибровки, наведение PTZ, детекции на карте). Мнемосхема Square —
холст в пикселях без привязки к местности: вид сбоку, изометрия, SCADA-подобная схема с регионами,
куда выводятся живые значения. Переносятся обе; это не дублирование, а разная семантика.

**Старая интеграция сносится в самом начале**, Square-репозиторий архивируется после переноса.

**Обратной совместимости нет и не нужно** (пользователь, 2026-10-28): ни тем, ни другим
продуктом пока никто не пользуется. Отсюда три следствия:
- Всё, что в Square посвящено **универсальной интеграции чужих систем** — реестр типов
  интеграций (`integro_types`, «Add new typed child»), самоописываемые команды (`actions.proto`,
  `action_*`), `IntegrationHost`/`IntegrationUtilsLib`/`SquareIntegrationClient`, топики
  `OnUpdateIntegros_*` — в объединённой системе не нужно и **не переносится**. Внешние продьюсеры,
  если понадобятся, подключатся позже к обычному gRPC `VmsMap` (`docs/future_plans`).
- Модули **сливаются**: ядро Square (`Domain`, `DbLayer`, `DataChangeLayer`) становится одним
  проектом `VmsMap`; тестовые/демонстрационные сервисы (`GrpcTracksClient`, `AASubService`,
  `BlinkService` как процесс) не переносятся.
- При переносе каждое решение оценивается и, если неудачно, **рефакторится сразу** (§3.1), а не
  копируется «как есть ради скорости».

**`VmsMap` — это «VmsCfg для карты».** Единственный владелец базы `ta_map` и источник истины о
дереве, геометрии, состояниях и событиях; web_srv и остальные сервисы ходят к нему по gRPC и
никогда в его базу. У Square контроллеры `LeafletAlarms` читали и писали БД напрямую — это одно из
решений, которое не переезжает.

## 3. Что берём из Square и как

| Модуль Square | Судьба |
|---|---|
| `Domain` (DTO, `ServiceInterfaces`, `VisualTypes`) | → в проект `VmsMap` (папка `Domain/`), пространства имён `VmsMap.*`; DTO интеграций (`Integro/`, `Actions/`) не берутся |
| `DbLayer` (`PgDbContext`, `pg_services/*`, `Models`) | → `VmsMap/Db/`; подключение по контракту ta_vms; `IRequestContextProvider` убирается; сервисы `IntegroService`, `ActionsService`, `GroupService` не берутся; дефекты и смеллы чинятся при переносе (§3.1) |
| `DataChangeLayer` | сливается с `DbLayer`-сервисами: это тонкие обёртки (`EventsUpdateService` — 19 строк), отдельный слой им не нужен; `StatesUpdateService` остаётся как место агрегации тревог |
| `PubSubLib` | не берётся: публикация через Dapr как в остальных сервисах ta_vms; из топиков остаётся по смыслу только `map-config-changed` |
| `IntegrationServerLib` | `GeometryProtoConvert`, `ObjectIdGenerator` — в `VmsMap`; `FileSystemService` (файлы на томе) заменяется хранением в БД (§4.6) |
| `IntegrationHost`, `IntegrationUtilsLib`, `GrpcDaprLib`, `Protos/*` | **не переносятся**: протокол продьюсеров не нужен. Контракт `VmsMap` пишется заново в `proto/vms_map.proto` (package `vms.map`) в стиле остальных proto ta_vms; сообщения Square (`ProtoObjectStates`, `EventProto`, `DiagramProto`…) — образец для форм, не контракт |
| `BlinkService` (`AlarmActor`, `AlarmStateAccumulator`, `HierarhyStateService`) | логика переносится в `VmsMap` как обновление `alarm_states` в транзакции `UpdateStates` (рекурсивный CTE по `parent_id`); акторы не нужны. Форма таблицы `alarm_states` сохраняется |
| `LeafletAlarms/Controllers/*` | **кусками**: каждое действие → rpc `MapService`, контроллер в web_srv становится тонким (DTO ↔ proto, проверка прав §4.7). Не берутся: `IntegroController`, `RouterController`, `GroupController`, `MapController.GetTiles` (у web_srv есть `api/map/tiles`), `FilesController.upload_static`, `AuthController` |
| `LeafletAlarms/Services/StateWebSocket.cs`, `WebSockListService.cs` | идея поллера изменений переезжает в `VmsMap` как один поллер на процесс, раздающий серверный gRPC-стрим; web_srv отдаёт SSE (образец — `AnalyticsLiveController`). См. §4.5 про стоимость |
| `LeafletAlarms/KMeans` | в `VmsMap` |
| `LeafletAlarms/Services/RightsCheckerService.cs`, `RightService` | в `VmsMap` как основа единой модели прав (§4.7): с наследованием по дереву и открытым набором действий, применяется ко **всем** чтениям (не только `GetByBox`); ACL камер из VmsCfg вливается сюда же |
| `ValhallaLib`, `RouterController`, `Build route` | не переносится (`docs/future_plans`) |
| `AASubService`, `GrpcTracksClient` | не переносятся: ONVIF-камеры — родная функция ta_vms; демо-данные для разработки — сид-скрипт против gRPC `VmsMap`, если понадобится |
| `tree_alarm.client` | только образец для UX и клиентских DTO (`store/Marker.ts`) |
| `SquareIntegrationClient` (сабмодуль и репозиторий) | удаляется из ta_vms; репозиторий архивируется вместе с Square |
| `non_docker_projects/*` (LogicMicroService: триггеры по зонам, счётчики треков) | не переносятся; идея «триггер по зоне» — в `docs/future_plans` |

### 3.1. Что рефакторится при переносе

Принцип: модуль берётся, но каждое решение в нём проверяется на пригодность для объединённой
системы; копировать известную ошибку — нельзя. Список кандидатов (пополняется по ходу):

- Дефекты из §1: `UpdateHierarchyAsync`, `RemoveAsync`, ключ `integro_types` (сама таблица
  уходит), PK у `events`, проверка права Control, права только в `GetByBox`.
- Статические кэши (`StatesUpdateService` — множество тревожных состояний, не инвалидируется)
  → чтение справочника / кэш с инвалидацией по записи.
- Обход дерева по одному запросу на узел (`MapService.GetAllChildren`) и по запросу на уровень
  (`GetByChildIdAsync`) → рекурсивный CTE.
- События: `Skip/Take` со смещением и сортировка по всем ключам **в памяти после** пагинации,
  объявленные и игнорируемые поля фильтра (`objects`, `property_filter`, `search_id`), `start_id`
  как «contains по имени», печать SQL в консоль → сортировка и keyset-пагинация в БД, фильтр по
  поддереву вместо таблицы `groups` (в едином дереве группа — это узел).
- Удаление объекта не чистит `object_states`, `alarm_states`, `events`, `tracks`, `rights` →
  каскад по FK или в одной транзакции.
- `GetOwnersAndViewsAsync` теряет обычные объекты, если во входе есть реплика → переписать.
- Реплики создаются фронтендом копированием свойств (`createDiagramReplica.ts`) → rpc
  `CreateReplica` в `VmsMap`.
- Магические свойства `__color`, `__image*`, `__image_rotate`, `__text` в универсальной
  `properties` → типизированная таблица `visual` (§4.2); `properties` только для абстрактных
  свойств объекта. Миграция `0001` сразу создаёт `visual`, переносить данные неоткуда.
- Размещение по региону — правило модели: узел с `region_id` не имеет собственной геометрии,
  сервис это проверяет при записи, а не только фронтенд при отрисовке.
- Неаутентифицированный WebSocket `/state` → SSE за аутентификацией web_srv.
- `diagrams.dgr_type` — имя типа строкой вместо id → FK на `diagram_types.id`;
  `DiagramCoordProto` без `rotation` (терялся при пуше) → в новом контракте поворот есть.
- `action_*`, `integro`, `integro_types`, `integro_type_children`, `groups` — не создаются.
- Файлы на томе `leaflet_data` → таблица в БД (§4.6).
- `KMeans.cs:77` TODO — посмотреть при переносе.
