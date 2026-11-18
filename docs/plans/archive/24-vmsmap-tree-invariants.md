# 24 — Инварианты дерева VmsMap

> Ветка `fix/vmsmap-tree-invariants` от `master` (`99c6e2fc`, 2026-11-17), только `ta_vms`.
> Объём — `dotnet/VmsMap` и `dotnet/VmsMap.Tests`; живой стек не нужен. Postgres-тесты идут на
> `ta_map_test` через `VMS_MAP_TEST_DSN`, как остальные пространственные.
> Источник — `docs/future_plans/24-vmsmap-tree-invariants.md`, пункты 1–5. Пункт 6 (геометрия)
> остаётся в файле 24: он не про дерево.

## Сверено с master

Все пять пунктов файла 24 подтвердились по коду; строки сместились мало, сути это не меняет:

- `MapDb.UpdateHierarchyAsync` (`dotnet/VmsMap/Db/Services/MapDb.cs:395`) по-прежнему разрешает
  `owner_id` только вверх (`ResolveOwnerAsync`, :552) и проверяет только циклы `parent_id`
  (`EnsureNoCycleAsync`, :581).
- `ResolveOwnerAsync` читает `FirstOrDefaultAsync` и не отличает «оригинал» от «узла нет»;
  `parent_id` не проверяется вовсе.
- `MapLocks` знает три блокировки (`CameraState`, `Alarm`, `DeviceDiagram`); глобальной
  иерархической нет.
- `SpatialDb.UpsertPlacementAsync` (`dotnet/VmsMap/Db/Services/SpatialDb.cs:393`) ищет строку
  `RowInSpaceAsync` и создаёт новую без всякой сериализации; индекс
  `idx_camera_placement_reference` ловит только второй эталон.
- Общие `UpdateObjects`/`DeleteObjects` (`MapServiceImpl.cs:121`, `:217`) обходят правила
  специализированных методов: `Delete` в `MapUpdateService` делает только пересчёт тревог.

## Порядок блокировок — решение

Сейчас транзитивный порядок карты: `camera-state → alarm → device-diagram`
(`DeviceDiagramService.EnsureAsync`/`AttachAsync`). Иерархическая блокировка добавляется
**последней**, а не первой: иначе `UpsertPlacementAsync` пришлось бы брать её в начале
транзакции, и все размещения камер сериализовались бы глобально, хотя блокировка задумана на
камеру.

Итоговый порядок (его же описывает комментарий в `MapLocks`):

`camera-state → camera-placement → alarm → device-diagram → hierarchy`

- **`hierarchy` — глобальный** advisory-lock. Цикл — свойство всего дерева, поэтому ключ не
  привязать к узлу. Правки дерева редки (конфиг, размещение, демо), горячего пути они не
  образуют; глобальность здесь дешевле, чем согласование ключей.
- `MapUpdateService.UpdateObjects` и `UpdateFigures` берут `alarm` **до** `UpdateHierarchyAsync`:
  они и так пересчитывают тревоги после записи, а обратный порядок дал бы deadlock с
  `DeviceDiagramService`. Для этого `MapUpdateService` получает `VmsMapDbContext` и открывает
  транзакцию, если её нет (в RPC-пути она уже открыта — вызовы к ней присоединяются).
- `UpsertPlacementAsync`/`RemovePlacementAsync` берут `camera-placement` до иерархии; удаление
  размещения пересчитывает тревоги в той же транзакции, поэтому размещение стоит перед `alarm`.
- `DeviceDiagramService` и `DemoSeeder` уже берут `camera-state`/`alarm` раньше записи дерева —
  их порядок не меняется.
- `UpdateHierarchyAsync` открывает свою транзакцию, если у вызывающего её нет: вне транзакции
  `pg_advisory_xact_lock` живёт до конца одного неявного оператора и не защищает ничего.

## Работы

### 1. Существование `owner_id` и `parent_id` (п. 2 файла)

**Что проверяем.** Новая строка или изменённое значение `parent_id`/`owner_id` должно указывать
на существующий узел. «Существует» = есть в БД (`existingEntities`) **или** в этом же пакете
(`dbUpdated`): родитель, создаваемый в том же вызове, законен.

**Чем отказываем.** `ArgumentException` с id: `Parent node '<id>' does not exist`,
`Owner node '<id>' does not exist`. Существующие проверки (`self parent`, `own owner`, цикл) уже
бросают `ArgumentException` — новый тип не вводим, RPC-статус не меняется.

**Где.** `MapDb.UpdateHierarchyAsync`, после чтения `existingEntities`, до разрешения владельца.
Проверяем только новые/изменённые значения: переименование узла с уже висящим (легаси)
`parent_id` не должно перестать работать. `ResolveOwnerAsync` получает вид «id → owner из
пакета» и отличает «строки нет» от «owner = null»: конец цепочки, которого нет в БД и нет в
пакете, — отказ, а не «оригинал».

**Заодно.** `MapUpdateService.UpdateProperties` читает текущую строку и передаёт в
`UpdateHierarchyAsync` её `parent_id`/`owner_id`/`name`: запись свойств не должна переписывать
дерево. Сейчас она молча ставит `parent_id = null` (в `ObjPropsDTO` его нет), и с правилами
п. 5 это стало бы отказом для плана вместо тихой потери родителя.

**Следствие для размещений.** `SpatialDb.EnsureCanonicalNodeAsync` (`SpatialDb.cs:995`) пишет
камеру под `ServerNodeId.For(camera.ServerId)`, не заводя серверный узел сам: в тестах и на
свежей установке он появляется только когда адаптер увидит сервер. С проверкой существования
первое размещение такой камеры упало бы. Поэтому `EnsureCanonicalNodeAsync` сначала заводит
серверный узел (как `DeviceNodeService.EnsureServerAsync`: id и имя — `serverId`), потом камеру;
повторная запись той же строки ничего не меняет.

**Тесты** (InMemory, `MapDbTests`): новый узел с несуществующим родителем; перенос на
несуществующего родителя; реплика с несуществующим владельцем; родитель и владелец из того же
пакета разрешены; переименование с легаси-`parent_id` не падает.

### 2. Оригинал с репликами нельзя понизить (п. 1 файла)

**Что проверяем.** У существующего узла `owner_id` меняется на непустой, а на этот узел уже
кто-то ссылается (`objects.owner_id`, индекс `idx_objects_owner_id`).

**Чем отказываем.** `ArgumentException`: `Node '<id>' has pictures and cannot become a copy of
'<owner>'`. Группу реплик автоматически не переписываем.

**Где.** `MapDb.UpdateHierarchyAsync`, после проверки существования владельца, одним запросом
по всем изменяемым id.

**Тест** (`MapDbTests`): A с репликами A1/A2; `A.owner_id = B` — отказ, A остаётся оригиналом.

### 3. Гонка переносов (п. 3 файла)

**Что проверяем.** Два параллельных переноса A→B и B→A не должны оба пройти. Проверка цикла —
чтение дерева, поэтому чтение и запись должны быть под одним замком.

**Где блокировка.** `MapLocks.AcquireHierarchyAsync` (ключ `ta_map_hierarchy`), вызывается в
начале `MapDb.UpdateHierarchyAsync` — до чтения `existingEntities` и до `EnsureNoCycleAsync`.
`EnsureNoCycleAsync` не меняется. `pg_advisory_xact_lock` по-прежнему пишется только в
`MapLocks` (grep-тест уже есть); добавляем в `MapLockTests` проверку, что иерархическую
блокировку берёт только `MapDb.cs`.

**Тест** (Postgres, два контекста на одной БД): A под R1, B под R2; одновременно
`UpdateObjects(A→B)` и `UpdateObjects(B→A)`; ровно один вызов падает `ArgumentException`, в
дереве нет цикла, проигравший узел остался на прежнем месте.

### 4. Одно размещение камеры на холст (п. 4 файла)

**Что проверяем.** Два одновременных `UpsertPlacementAsync` на один холст при уже существующем
эталоне в другом месте создают две картинки.

**Где блокировка.** `MapLocks.AcquireCameraPlacementAsync(cameraId)` — ключ по id камеры, берётся
в `UpsertPlacementAsync` сразу после `BeginTransactionAsync`, до `RowInSpaceAsync`, `AnyAsync`
эталона и создания узла. `RemovePlacementAsync` берёт её же: удаление и вставка на одном холсте
не должны пересекаться.

Хранимый `space_id` + `UNIQUE (camera_id, space_id)` не берём (решение файла 24): холст
производный (`geo_objects.canvas_id`) и разъедется при переносе узла. Схема не меняется.

**Тест** (Postgres, два сервиса/контекста): эталон на карте; два параллельных размещения на
плане; `camera_placement` — ровно две строки (по одной на холст), на плане не больше одной;
`ListPlacementsAsync` возвращает два размещения.

### 5. Одни правила «кто чей родитель» (п. 5 файла, перенос)

**Где.** Новый `dotnet/VmsMap/Db/Services/TreeRules.cs`, вызывается из `UpdateHierarchyAsync`
только при **смене родителя у существующего узла**: типизированный узел опознаётся по боковой
строке, а у создаваемого в том же вызове её ещё нет — пути создания и так специализированные.

**Таблица правил** (тип ребёнка ← допустимые типы родителя):

| Ребёнок | Родитель | Отказ |
|---|---|---|
| пространство-карта (`spaces.kind = map`) | только корень | `A map is always a root` |
| пространство-план | пространство (карта или план) | `A plan must stand inside a space` |
| канонический узел камеры (`Camera`, `owner_id = null`) | обычный узел (`Object`) | `A camera's own node stands under its server, not under a map object` |
| остальные (контейнер, реплика, ориентир, диаграмма, часть) | без ограничения | — |

`Object` — узел без боковой строки: серверный узел камеры и контейнер. Отличить сервер от
контейнера в `UpdateHierarchyAsync` нечем (типа сервера в схеме нет), и это сознательное
приближение: правило запрещает ровно то, что запрещает модель, — пространство/ориентир/камеру/
диаграмму в роли родителя канонического узла. Типы берутся из `NodeKindLookup` одним запросом
на пакет; `map`/`plan` — из `spaces`.

**Тесты** (`MapDbTests`): карта переносится под обычный узел — отказ; каноническая камера под
пространство — отказ; камера под серверный узел — разрешено; план под план — разрешено; реплика
в контейнер — по-прежнему разрешено (существующий `SpatialDbTests`).

### 6. Общее удаление проходит те же шаги (п. 5 файла, удаление)

**Где.** `SpatialDb.RemoveSubtreeAsync(ids, ct)` — одно место. Его вызывают:
`MapServiceImpl.DeleteObjects` (вместо `MapUpdateService.Delete`), `RemoveSpaceAsync`,
`RemoveLandmarkAsync`, `RemovePlacementAsync`; `DeletePictureAsync` исчезает.

**Шаги** (по образцу `RemoveSpaceAsync`, в одной транзакции):
1. поддерево (`GetSubtreeIdsAsync` — по `parent_id` и `owner_id`);
2. `camera_placement` в поддереве → id камер; строки удаляются;
3. ориентиры поддерева → их наблюдения (`ptz_geo_observation`, `ground_observation`) → id
   камер-наблюдателей; наблюдения и строки ориентиров удаляются явно (для InMemory и порядка FK);
4. `geo_objects` поддерева; `device_diagram_parts` и `device_diagrams` поддерева — в два
   `SaveChanges`, как в `DeletePictureAsync`;
5. `MapUpdateService.Delete(ids)` — счётчик и пересчёт тревог;
6. после коммита — `CameraArmingReconciler.ReconcileManyAsync(cameras)` и
   `IMapConfigNotifier.PublishAsync(ids, cameras)`.

Схема не меняется: остальные боковые строки уходят каскадом, как и раньше у `RemoveSpaceAsync`.

**Тесты** (Postgres): удаление ориентира общим путём — наблюдения и строки ушли, наблюдатель
камеры попал в `map-config-changed`; удаление плана общим путём — то же, что `RemoveSpaceAsync`
(поддерево, размещения, тревоги, камеры в уведомлении); удаление картинки общим путём уносит
диаграмму и её части.

### 7. Проверка

- `dotnet build dotnet/ta_vms.sln` (или как минимум `VmsMap` + `web_srv.Server`);
- `VMS_MAP_TEST_DSN=... dotnet test dotnet/VmsMap.Tests` — зелёные, включая новые Postgres-тесты;
- `dotnet test dotnet/web_srv.Tests` — зелёные;
- живой стек не поднимается; браузерная приёмка не нужна.

## Не входит

- Пункт 6 файла 24 (`GetGeoAsync` `Take(10000)`, проглоченные исключения) — остаётся в
  `docs/future_plans/24-vmsmap-tree-invariants.md`.
- FK на `owner_id`/`parent_id`, `tree_kind`, триггеры на противоречивые боковые строки — как и
  решено в файле 24, не берём.
- Правило «оригинал один уровень» для уже испорченных данных — не чиним автоматически.

## Сделано (2026-11-17)

Пункты 1–5 закрыты: `3f7044e0` — проверки существования владельца/родителя, запрет понижения
оригинала, правила `TreeRules`, иерархическая и камерная блокировки, порядок с `alarm`, запись
свойств больше не двигает узел; `f4ff62a7` — общее удаление через `SpatialDb.RemoveSubtreeAsync`
и серверный узел при первом размещении. Тесты: `VmsMap.Tests` — 370 (включая Postgres на
`ta_map_test`), `web_srv.Tests` — 20. Файл 24 сокращён до пункта 6 (геометрия).

## Коммиты

По одному на пункт, английскими сообщениями: проверки существования и понижения → блокировка
иерархии и порядок с `alarm` → блокировка размещения → правила родителей → общее удаление;
тесты идут вместе с кодом, которым они проверяются. Пуш и мерж — только по просьбе.
