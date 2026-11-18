# 24 — фронтенд: честный признак усечения холста

> Ветка `feat/map-truncated-banner` от `master` (`3ac4951f`, 2026-11-18); `fix/vmsmap-geo-honesty`
> там уже смержен (`1ba6ed10`, `97506447`), `GetByBox` отдаёт `Figures.truncated`.
> Объём — `dotnet/web_srv.client` (DTO, стор, компонент уведомления, три места отрисовки
> холста, переводы) и `docs/`. Сервер, proto и `web_srv` не меняются.
> Источник — «Что осталось» в `docs/future_plans/24-vmsmap-tree-invariants.md`; серверная часть —
> `docs/plans/24-vmsmap-geo-honesty.md`.

## Сверено с master

- `MapFigures` (`dotnet/web_srv.client/src/dto/mapDtos.ts:55`) — `{ figs?, addTracks? }`;
  `truncated` в типе нет.
- `mapApi.getByBox` (`api/mapApi.ts:84`) возвращает `MapFigures` без разбора: поле доедет до
  клиента, как только появится в типе.
- Единственный читатель холста — `loadCanvas` (`store/objectsSlice.ts:66`): берёт
  `response.figs ?? []` и остальное выбрасывает. Страница холста — `CanvasPage`
  (`objectsSlice.ts:12`) `{ ids, loaded }`; собирается только в `loadCanvas.fulfilled` (:301).
- Холст рисуют трое, все через эту же страницу:
  - оператор — `MapsOperatorPanel` через `useMapSpace`, средняя колонка
    (`MapsOperatorPanel.tsx:649`);
  - редактор — `SpaceEditor` через `useMapChildren`, ветка `SpaceCanvas` (`SpaceEditor.tsx:846`);
  - мнемосхема — `DiagramViewer` (`DiagramViewer.tsx:72`, `:95`).
- Шлюз отдаёт proto-JSON как есть (`Controllers/MapApi.cs:24`, `JsonFormatter.Default`):
  `truncated: true` появится в теле ответа, `truncated: false` formatter опускает как значение
  по умолчанию. Поэтому в TS поле — `truncated?: boolean`, а не обязательное.
- Компонентных тестов в клиенте нет: vitest гоняет `src/**/*.test.ts` в node без DOM
  (`vitest.config.ts`). Проверяется стор, уведомление — глазами.

## Решения

### 1. Флаг живёт на странице холста, а не в компоненте

`CanvasPage` получает `truncated: boolean`; `loadCanvas` кладёт туда `response.truncated === true`
(отсутствие поля — `false`: `GetByIds` его не отдаёт, как и сервер старше фикса). Признак переживает
перерисовки и доступен всякому, кто рисует этот холст; `useMapSpace` и `useMapChildren` менять не
нужно — компонент уведомления берёт `canvasId` и читает селектор сам.

Watch-поток флаг не трогает, `canvasAdd`/`canvasRemove` его не сбрасывают: watch по прямоугольнику
усечён так же молча (остаток файла 24), и его сообщение не доказывает полноту холста. Сбрасывает
флаг только новый ответ `loadCanvas` — в том числе перечитывание после реконнекта, которое
`useMapSpace` уже делает.

### 2. Одно уведомление на все холсты

Новый `src/map/CanvasTruncatedNotice.tsx`: читает `s.objects.canvases[canvasId]?.truncated` и при
`true` рисует `Alert severity="warning"` с ключом `maps.canvas.truncated`, иначе `null`.
Не закрывается крестиком: это свойство текущего чтения, а не разовое сообщение — закрыть его
значит снова показывать обрезанный холст целым.

Ставится рядом с холстом, к которому относится:

- `MapsOperatorPanel` — над `SpaceCanvas` в средней колонке; колонка становится
  `flexDirection: 'column'` (`SpaceCanvas` уже `flex: 1`). У `DiagramViewer` своё уведомление;
- `SpaceEditor` — в ветке `SpaceCanvas`, рядом с уже существующим `Alert` ошибки;
- `DiagramViewer` — между панелью инструментов и листом.

### 3. «Сузить прямоугольник» на языке этого UI

Клиент не вводит прямоугольник: он читает холст целиком по `canvasId`, и предел сервера
(`GeoService.DefaultBoxCount`, 10 000) применяется к нему. Сузить — значит открыть вложенное
пространство или план: у него собственный холст и собственный предел. Об этом и говорит текст.
Само число в перевод не пишем — это константа сервера, и дубликат в UI разойдётся с ней при
изменении.

- en: “The canvas is shown in part: it holds more figures than one read returns. Open a nested
  space or plan to see the rest.”
- es: “El lienzo se muestra parcialmente: contiene más figuras de las que devuelve una lectura.
  Abra un espacio o plano anidado para ver el resto.”

## Работы

1. `dto/mapDtos.ts` — `truncated?: boolean` у `MapFigures` с комментарием, повторяющим proto.
2. `store/objectsSlice.ts` — `CanvasPage.truncated`; `loadCanvas` возвращает `truncated`;
   `loadCanvas.fulfilled` пишет его в страницу.
3. `src/map/CanvasTruncatedNotice.tsx` — компонент.
4. Три места отрисовки: `MapsOperatorPanel`, `SpaceEditor`, `DiagramViewer`.
5. `i18n/locales/en/translation.json`, `i18n/locales/es/translation.json` — `maps.canvas.truncated`.
6. Тесты стора.
7. Docs: сократить `docs/future_plans/24-vmsmap-tree-invariants.md` до двух оставшихся пунктов,
   поправить его строку в `docs/future_plans/README.md`, дописать отметку о сделанном сюда.

## Тесты

`store/objectsSlice.test.ts`:

- `loadCanvas.fulfilled` с `truncated: true` ставит `canvases[id].truncated === true`;
- ответ без поля — `truncated: false`;
- повторное чтение без поля сбрасывает поднятый флаг (холст снова цел);
- существующие ожидания `{ ids, loaded }` дополнить `truncated: false`.

## Проверка

- `cd dotnet/web_srv.client && npm test` (vitest).
- `npm run build` — `tsc -b` и vite: типы DTO и всех мест отрисовки.
- `npm run lint`.
- Живой стек: уведомление требует холста больше 10 000 фигур, руками такое не набирается.
  Если стек поднят — проверить подменой ответа `POST /api/map/objects/by-box` на
  `{"figs":[],"truncated":true}` (Playwright route fulfil) на операторе и в редакторе и убедиться,
  что `Alert` виден над холстом; иначе ограничиться сборкой и тестами. После проверки ничего не
  оставлять запущенным.

## Что останется в файле 24

- Watch по прямоугольнику по-прежнему молча усечён: в сообщении SSE поля для признака нет
  (см. пункт выше — флаг уведомления это не лечит).
- Остаток обрезанного холста нельзя дозапросить: курсор по `id` ляжет на уже детерминированный
  порядок, когда появится потребность; до тех пор — сужение пространства, а на масштабе
  [17-scale-bench-100k.md](../../future_plans/17-scale-bench-100k.md) — серверная агрегация вместо
  выдачи всех фигур.

## Коммиты

1. этот план;
2. DTO, стор, компонент, три места и переводы;
3. тесты;
4. документация (файл 24, README, отметка здесь).

## Сделано (2026-11-18)

- `MapFigures.truncated?: boolean` с комментарием как в proto; `CanvasPage.truncated`;
  `loadCanvas` кладёт `response.truncated === true`, `loadCanvas.fulfilled` пишет его в страницу
  (`3329ef5d`).
- `src/map/CanvasTruncatedNotice.tsx` — `Alert severity="warning"`, не закрываемый, ключ
  `maps.canvas.truncated` (en/es). Поставлен над холстом у оператора (`MapsOperatorPanel`),
  в редакторе (`SpaceEditor`) и в мнемосхеме (`DiagramViewer`) (`3329ef5d`).
- Тесты: флаг поднимается чтением и сбрасывается следующим полным; thunk превращает отсутствие
  поля в `false`, `true` доводит до страницы; существующие ожидания дополнены `truncated: false`
  (`5d007464`).
- Прогоны: `npm test` — 77 тестов зелёные; `npm run build` (`tsc -b` + vite) — 0 ошибок;
  `npm run lint` — два **уже существующих** на `master` отказа в
  `src/components/objects/NodeSettingsCard.tsx` (react-hooks/set-state-in-effect), к этой ветке
  не относятся; в изменённых файлах отказов нет.
- Живой стек не поднимался: уведомление требует холста больше 10 000 фигур, а подмену ответа
  `by-box` без стека проверить не на чем. Компонент держится на тестах стора и на сборке.
- Отклонений от плана нет.
