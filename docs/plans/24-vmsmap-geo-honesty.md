# 24 — VmsMap: честное чтение геометрии холста

> Ветка `fix/vmsmap-geo-honesty` от `master` (`f831e8a5`, 2026-11-18), только `ta_vms`.
> Объём — `dotnet/VmsMap` и `dotnet/VmsMap.Tests`; живой стек не нужен. Postgres-тесты идут на
> `ta_map_test` через `VMS_MAP_TEST_DSN`, остальное — InMemory.
> Источник — раздел «Что осталось» в `docs/future_plans/24-vmsmap-tree-invariants.md`. Инварианты
> дерева закрыты 2026-11-17 (`docs/plans/24-vmsmap-tree-invariants.md`) и здесь не трогаются.

## Сверено с master

Всё из «Что осталось» подтвердилось:

- `GeoService.GetGeoAsync` (`dotnet/VmsMap/Db/Services/GeoService.cs:25`) берёт
  `limit = box.count > 0 ? count : 10000` (:27) и делает `query.Take(limit)` (:90) без
  `OrderBy`. Порядок строк отдаёт база, и он вправе меняться между вызовами.
- `GetGeoObjectsAsync` (:95) и `GetGeoObjectAsync` (:127) ловят `catch (Exception)` (:118,
  :143) и возвращают пустой словарь/`null`: отказ БД неотличим от «на холсте ничего нет».
- Вызывающие:
  - `MapServiceImpl.GetByBox` (`dotnet/VmsMap/Grpc/MapServiceImpl.Reads.cs:136`) — единственный
    пользователь `GetGeoAsync` в gRPC; ответ — `MapProto.Figures`, где признака усечения нет.
  - `WatchSnapshotService.GeoAsync` (`dotnet/VmsMap/Watch/WatchSnapshotService.cs:38`) — обёртка
    для `MapWatchPoller`; усечение там тоже молчаливое.
  - `MapServiceImpl.GetObjProps` (:299) — `GetGeoObjectAsync`; `GetByIds`/`GetByParams` через
    `FiguresAsync` (:848) — `GetGeoObjectsAsync`.
- Класса `AclService` в репозитории нет (он назван в задаче): права считают
  `IRightsService`/`RightService`/`SpatialDb`, и эти три метода геометрии они не вызывают.
  Менять там нечего.

## Решения

### 1. Порядок — `OrderBy(t => t.id)`

Перед `Take` в `GetGeoAsync` и в `GetGeoObjectsAsync` — сортировка по `id`. Это и делает
усечение воспроизводимым: один и тот же холст при одном и том же `count` отдаёт один и тот же
набор. Порядок GUID — свойство провайдера (Postgres сравнивает байты, InMemory — `Guid.CompareTo`),
но внутри одной установки он стабилен; межбазового равенства мы не обещаем и в тестах не
проверяем.

### 2. Усечение — явный флаг в ответе, не пагинация

`GetGeoAsync` возвращает не словарь, а `GeoResultDTO` (`Objects` + `Truncated`). Предел
остаётся прежним (`box.count`, иначе 10000), но читается `limit + 1` строк: если пришла
лишняя, `Truncated = true`, а лишняя отбрасывается. При `limit == int.MaxValue` лишняя строка
не запрашивается (переполнение `int`).

В proto у `Figures` появляется `bool truncated = 3`; `GetByBox` заполняет его на обоих путях
ответа — «холст целиком» и кластеризованном. `GetByIds`/`GetByParams` оставляют `false`:
там предел задаёт сам вызывающий и список идентификаторов.

**Почему флаг, а не пагинация.** Клиент карты рисует холст одним ответом и кластеризует его
на сервере; курсор потребовал бы цикла в браузере и второго контракта на тот же запрос.
Файл 24 допускает любой из двух вариантов, а честность здесь — «сказать, что ответ неполон»,
чтобы клиент сузил прямоугольник. Если когда-нибудь понадобится именно остаток, курсор по
`id` ложится на уже детерминированный порядок — это записано в остаток файла 24.

**Что флаг значит при правах и кластеризации.** Права фильтруют уже усечённый набор, поэтому
видимых фигур может оказаться меньше `count` — флаг по-прежнему значит «чтение обрезано».
Кластеризация (`GetByBox`, путь без `canvas_id`) считается по обрезанному набору; флаг теперь
говорит клиенту, что центроиды построены не по всему прямоугольнику.

### 3. Отказ чтения — убрать `catch`, исключение идёт наверх

Из `GetGeoObjectsAsync` и `GetGeoObjectAsync` удаляются оба `catch`. Пустые `ids` и
невалидный id остаются обычным «ничего» (пустой словарь/`null`) — это не ошибка чтения.

Исключение проходит `MapServiceImpl` и выходит из gRPC-обработчика: ASP.NET Core gRPC сам
логирует необработанное исключение метода на уровне Error и отдаёт клиенту `Unknown` — отказ
доходит и до вызывающего, и до лога, без второго логгера в `GeoService` (он потянул бы
`ILogger` в восемнадцать мест конструирования сервиса в тестах и дублировал бы лог фреймворка).
`MapWatchPoller` свои сбои уже логирует сам.

### 4. Вызывающие

- `MapServiceImpl.GetByBox`: `var read = await _geoService.GetGeoAsync(box)`, дальше
  `read.Objects`; `Truncated = read.Truncated` — в обоих `return`.
- `WatchSnapshotService.GeoAsync`: возвращает `read.Objects`; сигнатура не меняется.
  Снимок watch по прямоугольнику остаётся с тем же пределом и без признака усечения — в
  сообщении SSE для него места нет; это уходит в остаток файла 24.
- `CanvasQueryTests` — существующие обращения к `s.Geo.GetGeoAsync` получают `.Objects`.

## Работы

1. `dotnet/VmsMap/Domain/GeoDBDTO/GeoResultDTO.cs` — `record GeoResultDTO(Dictionary<string, GeoObjectDTO> Objects, bool Truncated)`.
2. `IGeoService.GetGeoAsync` → `Task<GeoResultDTO>`; `GeoService`: порядок, `limit + 1`,
   `GeoResultDTO`; в `GetGeoObjectsAsync` — порядок и снятый `catch`; в `GetGeoObjectAsync` —
   снятый `catch`.
3. `proto/vms_map.proto` — `bool truncated = 3` у `Figures` с комментарием; `MapServiceImpl.Reads`
   — оба пути `GetByBox`.
4. `WatchSnapshotService` — `.Objects`.
5. Тесты (ниже).
6. Docs: сократить `docs/future_plans/24-vmsmap-tree-invariants.md` до оставшегося, поправить
   строку в его README, дописать отметку о сделанном в этот план.

## Тесты

InMemory (`dotnet/VmsMap.Tests`):

- `CanvasQueryTests` — холст из трёх фигур с `count = 2` отдаёт ровно две, `Truncated = true`,
  а повторный вызов — те же два id (детерминизм); холст, который помещается, — `Truncated =
  false`. Там же gRPC: `GetByBox` по холсту с `count = 1` отдаёт одну фигуру и `Truncated = true`.
- Новый `GeoReadTests` (лёгкое окружение: InMemory `VmsMapDbContext` + `LevelService` +
  `GeoService`):
  - `GetGeoObjectsAsync` на освобождённом контексте бросает исключение, а не отдаёт пустой
    словарь; `GetGeoObjectAsync` — так же, а не `null`;
  - пустой id и id не-GUID у `GetGeoObjectAsync` — `null` без исключения (это «нет», не отказ);
  - пустой список `ids` — пустой словарь без исключения.

Postgres (`VMS_MAP_TEST_DSN`, `SpatialPostgresTests`):

- три фигуры на холсте, `count = 2`: две строки, `Truncated = true`, порядок совпадает с
  `OrderBy(g => g.id)` из той же БД, повторный вызов даёт тот же набор.

## Проверка

- `dotnet test dotnet/VmsMap.Tests` (InMemory) и с
  `VMS_MAP_TEST_DSN="Host=localhost;Port=5432;Database=ta_map_test;Username=postgres;Password=postgres"`
  — Postgres-тесты.
- `dotnet test dotnet/web_srv.Tests` — зелёные: proto-поле аддитивное, JSON web_srv отдаёт его
  как есть.
- `dotnet build dotnet/web_srv.Server` — генерация proto со стороны web_srv проходит.
- Живой стек не поднимается; браузерный клиент в этой задаче не меняется.

## Что останется в файле 24

- `MapFigures` во фронтенде (`dotnet/web_srv.client/src/dto/mapDtos.ts`) не знает `truncated`,
  и UI не показывает «показана часть холста». Сервер уже говорит правду; показать её — отдельная
  задача фронтенда.
- Снимок `Watch` по прямоугольнику сохраняет тот же предел и не сообщает об усечении: в
  сообщении SSE для этого нет поля.
- Остатка обрезанного холста по-прежнему нельзя дозапросить: если понадобится, курсор по `id`
  ложится на уже детерминированный порядок. До этого — сужение прямоугольника, а на масштабе
  файла 17 — серверная агрегация.

## Коммиты

1. этот план;
2. сервис, proto и вызывающие;
3. тесты;
4. документация (файл 24 и README).
