# 24 — VmsMap: честный watch по прямоугольнику

> Ветка `feat/watch-truncated` от `master` (`7945649c`, 2026-11-18; `feat/map-truncated-banner`
> уже смержена). Объём — `proto/vms_map.proto`, `dotnet/VmsMap`, `dotnet/VmsMap.Tests`,
> `dotnet/web_srv.client` и `docs/`; `dotnet/web_srv.Server` не меняется: SSE — это та же строка,
> которую пишет `MapLiveController`, а `MapApi.Formatter` отдаёт сообщение целиком.
> Источник — последний пункт «Что осталось» в `docs/future_plans/24-vmsmap-tree-invariants.md`.
> Честность чтения холста закрыта в [`24-vmsmap-geo-honesty.md`](24-vmsmap-geo-honesty.md), показ
> усечения во фронтенде — в [`24-map-truncated-banner.md`](24-map-truncated-banner.md).
> Живой стек не нужен.

## Сверено с master

- `WatchSnapshotService.GeoAsync` (`dotnet/VmsMap/Watch/WatchSnapshotService.cs:40`) возвращает
  `(await _geo.GetGeoAsync(box)).Objects`: `GeoResultDTO.Truncated` уже есть, но выбрасывается
  ровно на этом вызове.
- `MapWatchPoller.PollBoxAsync` (`dotnet/VmsMap/Watch/MapWatchPoller.cs:94`) — единственный
  читатель снимка по прямоугольнику; шлёт `set_ids2update` и `set_ids2delete`, и больше ничего.
- `WatchSubscription` (`dotnet/VmsMap/Watch/WatchSubscription.cs`) хранит диффы
  (`GeoVersions`, `SentGeo`, ...); `ResetDiff` (`:112`) сбрасывает их при смене scope. Поля для
  усечения нет.
- `WatchMessage` (`proto/vms_map.proto:1452`) — только `action` и `items`; в JSON нет ни поля, ни
  отдельного сообщения.
- `MapLiveController.Live` (`dotnet/web_srv.Server/Controllers/MapLiveController.cs:92`) пишет
  `MapApi.Formatter.Format(stream.Current)` в `data:` — новое поле доедет до браузера без правок
  web_srv.
- Клиент: `MapWatchMessage` (`dotnet/web_srv.client/src/dto/mapDtos.ts:410`) — `action?`/`items?`;
  `useMapSpace.handleCanvas` (`dotnet/web_srv.client/src/hooks/useMapSpace.ts:88`) выходит раньше
  разбора действия, если `items` пуст; редьюсера, ставящего флаг усечения со стороны watch, нет;
  `CanvasTruncatedNotice` (`src/map/CanvasTruncatedNotice.tsx`) уже читает
  `s.objects.canvases[canvasId]?.truncated` и ничего менять не придётся.
- Холст и watch читают **один и тот же** прямоугольник: `loadCanvas` → `mapApi.getByBox({canvasId})`,
  watch → `mapLiveUrl({box:{canvasId}})`, `MapLiveController` кладёт `CanvasId` и `count` не задаёт;
  оба доходят до `GeoService.GetGeoAsync` с пределом 10 000 и порядком по `id` (`1ba6ed10`). Значит
  их ответы об усечении согласованы, и watch вправе говорить о полноте холста.
- Других подписчиков на box-scope нет: `mapLiveUrl({box})` зовёт только `useMapSpace:138`.
  `DiagramViewer` и `NodesAdminPanel` смотрят по ids, а id-scope не усекается вовсе.

## Решения

### 1. Усечение — отдельное edge-triggered сообщение `set_box_truncated`

В `WatchMessage` появляется `optional bool truncated = 3`. `optional` — чтобы поле имело presence:
`JsonFormatter.Default` пишет поле, если оно выставлено, даже когда значение `false`, и клиент
получает явное `{"action":"set_box_truncated","truncated":false}` вместо загадочного сообщения без
полей.

`PollBoxAsync` после применения диффа шлёт `set_box_truncated` с текущим значением, когда оно
отличается от последнего отправленного — **включая первый тик** подписки. Последнее отправленное
живёт в `WatchSubscription.SentTruncated` (`bool?`), `ResetDiff` сбрасывает его в `null`, поэтому
после смены scope (и на новом соединении) правда объявляется заново. Тик, в котором ничего не
изменилось, ничего не повторяет.

Сообщение шлётся первым — раньше `set_ids2update`/`set_ids2delete` того же тика: подписчик узнаёт,
что набор неполон, до того как по нему что-то нарисует.

**Только box-scope.** `PollIdsAsync` его не шлёт: id-scope перечисляет узлы явно, усечения там
нет. Это записано комментарием у поля в proto.

**Почему не флаг в каждом diff-сообщении.** Дифф-сообщений нет, пока ничего не изменилось:
только что подключившийся подписчик полного прямоугольника не узнал бы о полноте вообще ничего, а
подписчик пустого (по правам) набора — что чтение за ним всё-таки обрезано. Отдельное состояние,
объявленное на первом тике, говорит правду и на пустом наборе.

**Почему не пагинация и не REST.** Отклонено ранее: файл 24 допускает флаг или курсор, план
`24-vmsmap-geo-honesty.md` выбрал флаг для чтения, а watch — поток, которому нужен тот же признак,
а не второй контракт на тот же прямоугольник. Остаток по-прежнему не дозапрашивается.

**Права не меняют смысл флага.** Усечение считается до фильтра прав, поэтому видимых фигур может
оказаться меньше предела, а флаг всё равно значит «чтение обрезано». Ровно как у
`Figures.truncated`.

**Честная оговорка канала.** Очередь подписки — `DropOldest` на 1000 (`WatchSubscription.cs:18`):
под нагрузкой сообщение о смене флага может потеряться. Лечится переподключением (оно перечитывает
холст через `loadCanvas`) или следующей сменой усечения; повторять состояние каждый тик ради этого
не будем — это watch, а не гарантированная доставка. Оговорка — комментарием у `SendTruncated`.

### 2. Сервер: снимок отдаёт результат чтения целиком

`WatchSnapshotService.GeoAsync` возвращает `GeoResultDTO`, а не `Dictionary`: признак перестаёт
теряться на границе сервиса. `PollBoxAsync` берёт `geo.Objects` для версий и видимого набора, а
`geo.Truncated` — для сообщения. Флаг вычисляется до `TryApply` (там же, где дифф), записывается
внутри него и отправляется только если `TryApply` вернул `true`, — иначе тик относится к старому
scope.

`WatchSubscription.SentTruncated` сбрасывается в `ResetDiff` вместе с остальными диффами.

`Send` получает перегрузку с флагом (или отдельный `SendTruncated`), чтобы поле ставилось в одном
месте с `action`.

### 3. Клиент: флаг усечения холста ставит и снимает watch

- `MapWatchMessage` (`dto/mapDtos.ts`) получает `truncated?: boolean` с комментарием, повторяющим
  proto: поле есть только у `set_box_truncated`, отсутствие — `false` (сервер старше фикса или
  id-scope).
- Новый редьюсер `canvasTruncated({ canvasId, truncated })` в `objectsSlice`: страницы ещё нет
  (её `loadCanvas.fulfilled` не пришёл) — no-op, как у `canvasAdd`; иначе пишет флаг, `ids` не
  трогает.
- `useMapSpace.handleCanvas`: действие `set_box_truncated` разбирается **до** проверки
  `changed.length === 0` (у этого сообщения нет `items`); диспатчится `canvasTruncated`. Поток ids
  (`handleNodes`) не меняется.
- `CanvasTruncatedNotice` уже читает флаг со страницы — уведомление над холстом появится и
  исчезнет само, без правок компонента.

Это уточняет решение 1 предыдущего плана («Watch-поток флаг не трогает, сбрасывает только новый
ответ `loadCanvas`»): теперь трогает, потому что сервер в нём больше не молчит, а оба чтения
смотрят один и тот же прямоугольник одним пределом. Более новый ответ (watch или `loadCanvas` —
что пришло позже) и есть правда; переподключение перечитывает холст и потому восстанавливает
согласие после гонки.

`DiagramViewer`, `NodesAdminPanel` и `web_srv.Server` не меняются.

### 4. Документация

Пункт про `api/map/live` в `docs/maps-and-plans.md` дополняется строкой о `set_box_truncated`:
box-scope объявляет, полон ли прямоугольник, id-scope — нет.

## Работы

1. `proto/vms_map.proto` — `optional bool truncated = 3` у `WatchMessage` с комментарием.
2. `dotnet/VmsMap/Watch/WatchSnapshotService.cs` — `GeoAsync` → `GeoResultDTO`.
3. `dotnet/VmsMap/Watch/WatchSubscription.cs` — `SentTruncated` (`bool?`) и его сброс в
   `ResetDiff`.
4. `dotnet/VmsMap/Watch/MapWatchPoller.cs` — `geo.Objects`, вычисление смены флага, первая
   отправка `set_box_truncated`, перегрузка `Send`.
5. `dotnet/web_srv.client/src/dto/mapDtos.ts` — `truncated?: boolean`.
6. `dotnet/web_srv.client/src/store/objectsSlice.ts` — редьюсер `canvasTruncated`.
7. `dotnet/web_srv.client/src/hooks/useMapSpace.ts` — ветка `set_box_truncated` до раннего выхода.
8. Тесты (ниже).
9. Docs: `docs/maps-and-plans.md`; сократить `docs/future_plans/24-vmsmap-tree-invariants.md` до
   одного оставшегося пункта; поправить его строку в `docs/future_plans/README.md`; отметить
   сделанное в этом плане.

## Тесты

Сервер — новый `dotnet/VmsMap.Tests/WatchPollerTests.cs` (InMemory, окружение как в
`CanvasQueryTests.Create`: `VmsMapDbContext`, `MapDb`, `GeoService`, `StateService`, `ValuesService`,
`RightsService`; подписка через `WatchSubscription.SetBox`, тик через
`MapWatchPoller.PollAsync`, сообщения читаются из `subscription.Messages.Reader`). Вызывающий —
admin (`new CallerContext(null, ["admin"], true)`), чтобы права не примешивались.

- холст из трёх фигур, `count = 2`: первый тик даёт `set_box_truncated` с `Truncated == true`
  первым сообщением, и `set_ids2update` с теми же двумя id, что `OrderBy(id)` в БД;
- второй тик без изменений: второго сообщения `set_box_truncated` нет;
- холст, помещающийся в предел: первый тик даёт `set_box_truncated` с `Truncated == false`;
- после удаления фигуры набор снова помещается: следующий тик даёт `set_box_truncated` с
  `Truncated == false` (смена в обратную сторону);
- id-scope (`SetIds`): тик не даёт `set_box_truncated` ни в каком виде.

Клиент — `dotnet/web_srv.client/src/store/objectsSlice.test.ts`:

- `canvasTruncated` ставит `canvases[id].truncated === true` и снимает его, `ids` не трогает;
- для неизвестного холста — no-op, страницу не создаёт;
- существующие ожидания не меняются (поле `truncated` у `CanvasPage` уже есть).

Компонентных тестов в клиенте нет (vitest без DOM); связка «SSE-сообщение → редьюсер» держится на
серверном тесте формы сообщения и на сторе.

## Проверка

- `dotnet test dotnet/VmsMap.Tests` (InMemory) и с
  `VMS_MAP_TEST_DSN="Host=localhost;Port=5432;Database=ta_map_test;Username=postgres;Password=postgres"`
  — Postgres-тесты не должны сломаться: усечение там уже проверено, новый код от провайдера не
  зависит.
- `dotnet build dotnet/web_srv.Server` — генерация proto со стороны web_srv проходит (поле
  аддитивное).
- `dotnet test dotnet/web_srv.Tests`.
- `cd dotnet/web_srv.client && npm test`, `npm run build`, `npm run lint`.
- Живой стек не поднимается: усечение требует холста больше 10 000 фигур. Если стек уже поднят —
  подменить SSE-кадр нельзя (это поток), поэтому ограничиваемся тестами и сборкой. После проверки
  ничего не оставлять запущенным.

## Что останется в файле 24

- Остаток обрезанного холста нельзя дозапросить. Курсор по `id` ляжет на уже детерминированный
  порядок (`1ba6ed10`), когда появится потребность; до тех пор — сужение прямоугольника, а на
  масштабе [17-scale-bench-100k.md](../future_plans/17-scale-bench-100k.md) — серверная агрегация
  вместо выдачи всех фигур.

## Коммиты

1. этот план;
2. proto, сервер и подписка;
3. клиент (DTO, редьюсер, хук);
4. тесты;
5. документация (файл 24, README, maps-and-plans, отметка здесь).
