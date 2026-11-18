# 24 — VmsMap: остаток обрезанного холста дозапрашивается

> Ветка `feat/canvas-cursor` от `master` (`3b8502ff`, 2026-11-18; `feat/watch-truncated` смержена).
> Объём — `proto/vms_map.proto`, `dotnet/VmsMap`, `dotnet/VmsMap.Tests`, `docs/`.
> `dotnet/web_srv.Server` и `dotnet/web_srv.client` не меняются: шлюз разбирает
> `GetByBoxRequest` целиком и отдаёт ответ как есть, а фронтенд курсор сознательно не получает
> (решение 6). Живой стек не нужен.
> Источник — последний пункт «Что осталось» в `docs/future_plans/24-vmsmap-tree-invariants.md`
> (после этой работы — `docs/future_plans/legacy/`).
> Порядок и флаг усечения закрыты в [`24-vmsmap-geo-honesty.md`](24-vmsmap-geo-honesty.md), показ
> усечения — в [`24-map-truncated-banner.md`](24-map-truncated-banner.md), watch по
> прямоугольнику — в [`24-watch-truncated.md`](24-watch-truncated.md).

## Сверено с master

- `GeoService.GetGeoAsync` (`dotnet/VmsMap/Db/Services/GeoService.cs:29`) сортирует
  `OrderBy(t => t.id)` и читает `limit + 1`; лишняя строка даёт `Truncated` и отбрасывается
  (`:95–106`). Курсора нет: каждый вызов начинает с начала холста.
- `GeoResultDTO` (`dotnet/VmsMap/Domain/GeoDBDTO/GeoResultDTO.cs:8`) — только `Objects` и
  `Truncated`; точку разреза можно узнать лишь из последнего ключа словаря, а его порядок —
  деталь `Dictionary`, а не контракт.
- `MapServiceImpl.GetByBox` (`dotnet/VmsMap/Grpc/MapServiceImpl.Reads.cs:109`) — единственный
  gRPC-вызывающий; `Figures.truncated` заполняется на обоих путях ответа (`:150`, `:163`),
  признака продолжения нет.
- `MapServiceImpl.ToBoxDto` (`:1061`) собирает `BoxDTO` из `Box`; `GetByBox` получает его из
  `request.Box` (`:114`).
- `WatchBox` тоже оборачивает `Box`, и watch подставляет её через тот же `ToBoxDto`
  (`MapServiceImpl.Watch.cs:30`). Значит курсор в `Box` дал бы watch'у «область из середины», чего
  в модели watch нет.
- Шлюз разбирает запрос целиком: `MapReadControllers.GetByBox` — `MapApi.Parse<GetByBoxRequest>`
  (`dotnet/web_srv.Server/Controllers/MapReadControllers.cs:69`), ответ — `MapApi.Json` (:72).
  Новое поле доедет до HTTP и до браузера без правок web_srv.
- Фронтенд читает холст одним запросом `mapApi.getByBox({ canvasId })`
  (`dotnet/web_srv.client/src/api/mapApi.ts:84`, `store/objectsSlice.ts:73`) и о продолжении не
  просит; `CanvasTruncatedNotice` показывает предупреждение из `CanvasPage.truncated`.

## Решения

### 1. Курсор — параметр чтения, а не свойство области

`string after_id = 4` — в `GetByBoxRequest`, не в `Box`. `Box` — это область карты (прямоугольник,
зона, холст, слои, id), и её же получает `WatchBox`. Watch смотрит область целиком: его набор —
стабильный префикс области, и `set_box_truncated` говорит именно о полноте этого префикса. Курсор
же — «продолжи это чтение с места, где оно остановилось», свойство одного запроса. В
`GetByBoxRequest` он структурно недоступен watch'у, и объяснять это комментарием не нужно.

`BoxDTO` получает `Guid? after_id` — уже разобранный; `ToBoxDto` его не заполняет, `GetByBox`
ставит после разбора `request.AfterId`. `CopyBox` (путь треков) курсор не копирует: у треков своя
пагинация.

### 2. Курсор — точка разреза чтения, а не последняя видимая фигура

`Figures.next_cursor` — id последней строки, которую чтение **взяло** (последней оставшейся после
отбрасывания лишней). Он заполняется только при `truncated`; иначе пуст.

Нельзя брать последнюю фигуру **ответа**: права фильтруют уже усечённый набор в `GetByBox`
(`:136–140`), поэтому среди отброшенных и невидимых строк может оказаться хвост страницы. Если бы
курсор был id последней видимой фигуры, клиент на следующем шаге перечитывал бы невидимый хвост и
мог застрять: страница целиком из невидимых фигур вернула бы пустой ответ с тем же курсором.

Поэтому курсор — закладка чтения, а не узел ответа: он может назвать id, которого вызывающий не
видит. Ответ и так уже говорит `truncated`, что за пределом чтения что-то есть; id — непрозрачный
GUID и не выдаёт о невидимом узле ничего, кроме его положения в порядке. Это правило записывается
комментарием у поля в proto и у `GeoResultDTO.NextCursor`.

### 3. Строго после курсора, тем же порядком

В `GetGeoAsync` после всех фильтров:

```csharp
if (box.after_id.HasValue)
  query = query.Where(t => t.id.CompareTo(box.after_id.Value) > 0);
```

Строго — иначе страница повторит свою последнюю строку. `CompareTo` — тот же способ сравнения,
что уже стоит в `MapDb.GetByParentIdsAsync` и `TrackService`, и тот же порядок, что у
`OrderBy(t => t.id)`: сортировка GUID — свойство провайдера (Postgres сравнивает байты, InMemory —
`Guid.CompareTo`), но сравнение и порядок идут через одно и то же свойство, поэтому страницы
склеиваются в тот же порядок без дыр и повторов. Межбазового равенства по-прежнему не обещаем.

`after_id` не проверяется на принадлежность холсту: это позиция, а не id узла. Любой валидный GUID
— законный курсор; сочетание с другими фильтрами даёт упорядоченный префикс этого сочетания.

### 4. Невалидный курсор — `InvalidArgument`, а не пустой ответ

`GetByBox` разбирает `request.AfterId` в `Guid` на границе и при отказе бросает
`RpcException(StatusCode.InvalidArgument, ...)` — как `GetEvent` с `id` (`Reads.cs:544`). Молча
вернуть «дальше ничего» значило бы соврать: клиент принял бы конец холста за ошибку в курсоре.
Пустая строка — не курсор, а «с начала», как и отсутствие поля.

### 5. Кластеризованный ответ курсора не даёт

`GetByBox` без `canvas_id` и с 1000+ фигурами отвечает центроидами (`:154–164`), а не фигурами.
Центроиды — агрегат чтения, не его префикс: страницы центроидов разных подмножеств нельзя сложить
в карту. Поэтому на этом пути `next_cursor` остаётся пустым (флаг `truncated` остаётся: чтение
всё-таки обрезано), и это записывается комментарием у поля и у ветки. Холст (`canvas_id`) идёт
всегда целым путём и курсор получает; прямоугольник — пока ответ не кластеризуется, то есть до
1000 фигур. На масштабе [17-scale-bench-100k.md](../../future_plans/17-scale-bench-100k.md) для
больших прямоугольников нужна серверная агрегация, а не выдача всех фигур страницами.

### 6. UI не трогаем

Курсор — возможность API, а не кнопка «показать остаток», по трём причинам:

- **Две правды в одном флаге.** `CanvasPage.truncated` сейчас значит «одно чтение остановилось на
  пределе», и watch по прямоугольнику переставляет его из `set_box_truncated` — о **всей** области.
  Если бы клиент догрузил остаток страницами, область всё равно осталась бы обрезанной для watch'а,
  и он на переподключении или смене набора снова объявил бы холст частичным. Плюс каждое
  перечитывание (`loadCanvas` после правки, реконнект) собирает страницу заново из одного ответа и
  теряет догруженное — кнопка вела бы себя непредсказуемо.
- **Масштаб.** Целевой холст — сотни тысяч фигур (файл 17); догружать их все в браузер — ровно то,
  чего этот файл избегает. Честный путь оператора остаётся прежним и записан в уведомлении: открыть
  вложенное пространство или план.
- **Нет потребителя в UI.** `getByBox` в браузере зовётся только для холста
  (`loadCanvas`), и предупреждение уже говорит правду. Курсор закрывает находку на уровне чтения:
  остаток **можно** дозапросить — тем клиентом, которому он действительно нужен (экспорт,
  инструменты, будущий агрегированный вид).

`web_srv` не меняется: он не строит `GetByBoxRequest` сам, а разбирает тело целиком, и
`nextCursor` появится в JSON без правок (`JsonFormatter` опускает пустое значение, как у
`truncated`).

### 7. Курсор — позиция, а не снимок

Между страницами чтение не транзакционно: удалённая строка-курсор не мешает (сравнение значений),
добавленная фигура с id меньше курсора попадёт только в следующее полное чтение. Это обычная
оговорка keyset-пагинации; watch по прямоугольнику и перечитывание холста и есть её лекарство.
Оговорка — комментарием у `after_id` в proto, чтобы клиент не ждал снимка.

## Работы

1. `proto/vms_map.proto` — `string after_id = 4` у `GetByBoxRequest`; `string next_cursor = 4` у
   `Figures`; комментарии: точка разреза, права, кластеризация, отсутствие снимка.
2. `dotnet/VmsMap/Domain/GeoDTO/BoxDTO.cs` — `Guid? after_id` с комментарием.
3. `dotnet/VmsMap/Domain/GeoDBDTO/GeoResultDTO.cs` — третий член `string? NextCursor`.
4. `dotnet/VmsMap/Db/Services/GeoService.cs` — фильтр `CompareTo(...) > 0`; `NextCursor` из
   последней оставшейся строки при `truncated`, иначе `null`.
5. `dotnet/VmsMap/Grpc/MapServiceImpl.Reads.cs` — разбор `request.AfterId` (`InvalidArgument`),
   `box.after_id`, `whole.NextCursor` на целом пути; комментарий на кластеризованном.
6. Тесты (ниже).
7. Docs: абзац о курсоре в `docs/maps-and-plans.md`; `docs/future_plans/24-vmsmap-tree-invariants.md`
   — в `docs/future_plans/legacy/`, строка из таблицы README уходит; отметка «Сделано» здесь.

## Тесты

InMemory (`dotnet/VmsMap.Tests`):

- `CanvasQueryTests.ACutCanvasIsReadWholeAPageAtATime` — пять фигур на холсте, `count = 2`: первая
  страница — первые две в порядке `Guid` (а не строковом), `Truncated`, `NextCursor` — id второй;
  вторая страница с `after_id` — следующие две, снова `Truncated`, курсор — четвёртая; третья —
  последняя, `Truncated == false`, `NextCursor == null`. Склейка страниц равна всему холсту по
  порядку `Guid`, без повторов и пропусков.
- `CanvasQueryTests.ACursorAtTheLastFigureIsAnEmptyTail` — `after_id` последней фигуры: пусто,
  `Truncated == false`, курсора нет (граница страницы не повторяется).
- `CanvasQueryTests.ACursorNamesTheReadsCutEvenWhenRightsHideIt` — четыре фигуры, у `alice` право
  только на первую и последнюю, `count = 2`: первая страница отдаёт первую фигуру,
  `Truncated == true`, `NextCursor` — id второй (невидимой); вторая страница с этим курсором
  отдаёт последнюю фигуру и больше курсора не даёт. Склейка — ровно видимое.
- `CanvasQueryTests.GetByBoxCarriesTheCursorToTheClient` — gRPC-путь: `AfterId` первой страницы
  возвращает следующую, `Figures.NextCursor` заполнен; невалидный `AfterId` — `RpcException` со
  `StatusCode.InvalidArgument`.
- `CanvasQueryTests.AClusteredAnswerGivesNoCursor` — прямоугольник (без `canvas_id`) из 1000 фигур,
  `count = 1000`: ответ кластеризован, `Truncated == true`, `NextCursor` пуст.

Postgres (`VMS_MAP_TEST_DSN`, `SpatialPostgresTests`):

- `ACutCanvasIsPagedByItsCursor` — три фигуры, `count = 2`: вторая страница с `after_id` из первого
  чтения отдаёт третью; проверяет и трансляцию `Guid.CompareTo` в Npgsql, и совпадение с
  `OrderBy(g => g.id)` самой базы.

## Проверка

- `dotnet test dotnet/VmsMap.Tests` (InMemory) и с
  `VMS_MAP_TEST_DSN="Host=localhost;Port=5432;Database=ta_map_test;Username=postgres;Password=postgres"`
  — Postgres-тесты включаются.
- `dotnet build dotnet/web_srv.Server` — генерация proto со стороны шлюза проходит (поля
  аддитивные).
- `dotnet test dotnet/web_srv.Tests`.
- Живой стек не поднимается: усечение требует холста больше 10 000 фигур. После проверки ничего не
  оставлять запущенным.

## Что останется в файле 24

Ничего: последний пункт закрыт. Файл уходит в `docs/future_plans/legacy/` — «что из ревью не
берём» остаётся историей решения, — а строка в таблице `docs/future_plans/README.md` удаляется.

## Коммиты

1. этот план;
2. proto, DTO и сервис;
3. тесты;
4. документация (файл 24 в legacy, README, maps-and-plans, отметка здесь).

## Сделано (2026-11-18)

- `GetByBoxRequest.after_id = 4` и `Figures.next_cursor = 4` с комментариями (точка разреза,
  права, кластеризация, отсутствие снимка); `BoxDTO.after_id` (`Guid?`), `GeoResultDTO.NextCursor`;
  `GeoService` фильтрует `id.CompareTo(after_id) > 0` и берёт курсор из последней оставшейся строки
  (`16b2ba1a`). В `GetByBox` курсор разбирается на границе (`InvalidArgument` на не-UUID) и
  заполняет `NextCursor` только на целом пути; кластеризованный путь оставляет его пустым.
- Тесты: `CanvasQueryTests` — страницы склеиваются в холст по порядку `Guid`, курсор на последней
  фигуре даёт пустой хвост, страница, скрытая правами, всё равно называет точку разреза, gRPC
  проносит курсор и отвергает не-UUID, кластеризованный прямоугольник курсора не даёт; в
  `SpatialPostgresTests` — вторая страница после `after_id` совпадает с `OrderBy(g => g.id)` самой
  базы (`5c453ab4`).
- Прогоны: `dotnet test dotnet/VmsMap.Tests` — 391 зелёный с `VMS_MAP_TEST_DSN` на `ta_map_test`
  (Postgres-тест в том числе проверяет трансляцию `Guid.CompareTo` в Npgsql); `dotnet build
  dotnet/web_srv.Server` — 0/0; `dotnet test dotnet/web_srv.Tests` — 20 зелёных.
- Живой стек не поднимался: усечение требует холста больше 10 000 фигур.
- Тесты приведены к новому правилу `CLAUDE.md` «Test code: name the contract, do not spell out its
  position»: строчные имена сущностей, каждая повторяющаяся строка (имена пространств, узлов,
  пользователей, роль, имена сообщений watch) — именованная константа, именованные
  `pageSize`/`figureCount` вместо чисел, курсор проверяется через `Keys.Last()` / `Single().Id`, а
  не позицией в списке; в `SpatialPostgresTests` посев фигур вынесен в общий `SeedCanvasAsync`.
- Отклонений от плана нет: `web_srv` и `web_srv.client` не менялись, UI холст страницами не
  читает. Файл 24 перенесён в `docs/future_plans/legacy/` (ссылки в нём поправлены на
  `../../plans/`), строка из README удалена.
