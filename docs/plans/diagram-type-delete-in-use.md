# Удаление типа диаграммы, который используется

> Найдено при разборе ревью по [копии как точке входа для правки](archive/copy-as-edit-entry.md)
> (2026-11-26), жило в `docs/future_plans/26-deleting-a-diagram-type-in-use.md` и переехало сюда
> 2026-11-27, когда работа началась. Ветка: `fix/diagram-type-delete-in-use` от `master`.

## Решение

**Отказ, а не каскад** (принято 2026-11-27). Это третий угол того же инварианта, который уже
держат две проверки:

- `DiagramTypeService.EnsureRegionsAreFreeAsync` (`VmsMap/Db/Services/DiagramTypeService.cs:255`)
  не даёт убрать регион, в котором стоит узел;
- `DiagramService.EnsureChildrenKeepTheirRegionsAsync` (`VmsMap/Db/Services/DiagramService.cs:183`)
  не даёт сменить или снять тип, пока ребёнок стоит в ключе, которого новый тип не предлагает.

Каскад («снять тип у диаграмм и привязок») — тихая правка чужих строк, и он всё равно не
возвращает картинку: регионы и привязки частей уходят вместе с типом, так что вернуть тип обратно
не значит вернуть фон.

## Что установлено к началу

- `DiagramTypeService.DeleteAsync` (`DiagramTypeService.cs:23–46`) удаляет строки словаря без
  единой проверки: конвертация object id → `Guid`, `RemoveRange`, `SaveChanges`.
- Единственный путь, которым это вызывается снаружи, — `MapServiceImpl.DeleteDiagramTypes`
  (`VmsMap/Grpc/MapServiceImpl.cs:418–423`); ловушки `DiagramTypeConfigException` в нём **нет** —
  она есть только у `UpdateDiagramTypes` (`:402–412`), который отдаёт её как `InvalidArgument`.
- Две ссылки на тип: `dbContext.Diagrams` (`DBDiagram.dgr_type`, `Guid?`) — диаграмма нарисована
  из типа; `dbContext.DeviceDiagrams` (`DBDeviceDiagram.diagram_type_id`, `Guid?`) — привязка
  устройства держит диаграмму по шаблону. В схеме обе — внешние ключи с `ON DELETE SET NULL`
  (`Grpc/migrations/0000_init.sql`), то есть база уже умеет молча обнулить ссылку; отказ сервиса —
  то, что не даёт этому случиться. EF этих связей не знает (навигаций нет), поэтому проверка
  читает их сама, и только у живых узлов: `diagrams.id` и `device_diagram.object_id` каскадятся с
  `objects`.
- Комментарий у привязки (`VmsMap/Db/Models/Diagrams/DBDeviceDiagram.cs:18`) сейчас говорит «Null
  once the type has been deleted: the canvas and its replicas stay, drawn by nothing». После
  отказа этот путь закрывается — комментарий надо переписать, иначе он врёт.
- **Демо от отказа не ломается.** `DemoSeeder.DeleteOrder` (`Demo/DemoSeeder.cs:29–30`) —
  `object → camera → landmark → space → type → level → file`, а диаграмма устройства
  регистрируется как объект мира (`:463`: «the node's delete is what also takes its replicas and
  the service's row for it»). К моменту удаления типа ни диаграмм, ни привязок, названных им, у
  демо не остаётся. Регрессия проверяется демо-тестами, менять сидер не нужно.
- **GUI уже готов**: `MapDiagramTypesDialog` ловит ошибку и рисует её рядом со списком
  (`dotnet/web_srv.client/src/components/diagrams/MapDiagramTypesDialog.tsx:130–150`), а
  `errorText` переводит `InvalidArgument` в текст. Кода во фронте нет, нужна только живая проверка.

## Шаги

1. **Проверка в `DeleteAsync`.** До `RemoveRange`, в порядке исходного запроса, а не в порядке
   выдачи БД: `Where(Contains)` не гарантирует ни порядок строк, ни порядок `guidIds`, поэтому
   собрать `toDelete.ToDictionary(t => t.id)` и идти по `guidIds` — клиентскому порядку — падая на
   первом найденном:
   - диаграмма с `dgr_type == type.id` → `DiagramTypeConfigException`
     `The diagram type "<name>" cannot be deleted: the diagram "<object id>" is drawn from it.`;
   - привязка с `diagram_type_id == type.id` → `… : the device "<source_kind>:<source_id>" keeps a
     diagram of it.`;
   - id печатается через `Utils.ConvertGuidToObjectId`, как это делает
     `EnsureChildrenKeepTheirRegionsAsync`.
   Проверки и удаление идут одним экземпляром `DbContext`, изменения фиксирует один `SaveChanges` —
   поэтому запрос из нескольких id либо проходит целиком, либо не удаляет ничего. Атомарности
   «проверка + удаление» при этом нет: между чтением и коммитом может появиться новая ссылка.
   Это та же гонка, что у соседних проверок, и она остаётся темой
   [24-map-lock-audit](../future_plans/24-map-lock-audit.md).
2. **gRPC.** Обернуть вызов в `try/catch (DiagramTypeConfigException)` в
   `MapServiceImpl.DeleteDiagramTypes` и отдать `RpcException(StatusCode.InvalidArgument, …)` —
   ровно как `UpdateDiagramTypes`. У границы свой тест (шаг 4), потому что без него сервисные
   тесты остаются зелёными, а клиент получает `Internal`.
3. **Комментарий у `DBDeviceDiagram.diagram_type_id`.** Перед правкой — семантика (проверено
   2026-11-27): `null` не наследие удаления, а **штатное состояние** «шаблона пока нет». Привязка
   без шаблона создаётся с `diagram_type_id = diagram.dgr_type` (`Adapter/DeviceDiagramService.cs:261`),
   а `dgr_type` может быть пуст; `DeviceDiagramReconciler` (`:69–73`) такой строке подбирает
   шаблон, как только устройство назвало модель. Удаление типа было вторым путём к тому же
   состоянию — его закрывает отказ. Комментарий переписать под обе семантики и убрать «Null once
   the type has been deleted».
4. **Тесты стражей** — новый файл `DiagramTypeDeleteTests` (in-memory, как `DiagramTemplateTests`):
   - `ADiagramDrawnFromTheTypeCannotBeDeleted` — тип, узел-диаграмма с `dgr_type`; ожидание
     `DiagramTypeConfigException` с object id диаграммы, тип на месте после отказа;
   - `ADeviceDiagramKeptFromTheTypeCannotBeDeleted` — строка `DeviceDiagrams`, ожидание отказа;
   - `DeletingOneOfSeveralTypesChecksOnlyItsOwnReferences` — в одном запросе свободный тип и
     используемый: отказ, и свободный тоже остался (ни одной строки не удалено);
   - `AFreeTypeIsDeleted` — свободный тип удаляется без исключения;
   - `TheRefusalReachesTheRpcCallerAsInvalidArgument` — прямой вызов
     `MapServiceImpl.DeleteDiagramTypes`: `RpcException`, `StatusCode.InvalidArgument`, в сообщении
     имя типа, тип остался в БД. Тест in-memory (маппинг не зависит от провайдера): добавить в
     `TestWiring` фабрику наподобие `MapPostgres.Grpc`, где `diagramTypeService` не `null!`. Иначе
     без `VMS_MAP_TEST_DSN` он молча пропускается — а в CI эта переменная не ставится.
5. **Тест каскада — только Postgres** (`MapDiagramWritePostgresTests`, `[Collection("postgis")]`):
   `DeletingAFreeTypeTakesItsRegionsAndBindings` — после удаления свободного типа его строки в
   `diagram_type_regions` и `diagram_type_parts` уходят, и **ничего чужого не тронуто**: регионы и
   привязки второго, живого типа, сама диаграмма и строка `device_diagram`. In-memory провайдер
   каскад БД не выполняет, поэтому без него этот пункт не проверить.
6. **Живая проверка в GUI** (стек не гасить), три случая: тип, которым нарисована обычная
   диаграмма — текст ошибки в окне словаря; тип, который держит привязка устройства — тот же отказ;
   свободный тип — уходит вместе с регионами. Третий случай — контроль: отказ не должен мешать
   штатному каскаду собственных детей типа.

## Где используется: список в редакторе типа

> Добавлено 2026-11-27 по решению пользователя. Отказ без объяснения — тупик: администратору нужно
> видеть, куда идти, чтобы освободить тип. Список — удобство интерфейса, а не механизм целостности:
> проверка при удалении (шаги 1–2) работает и без него, и остаётся последним словом.

**Производная информация, ничего не храним.** Источники те же два: `diagrams.dgr_type` и
`device_diagram.diagram_type_id`. Отдельная таблица использования разошлась бы с ними.

**Строка списка — место, а не диаграмма** (замечание пользователя про реплики): у каждой
используемой диаграммы показываются и её собственный узел, и все реплики (`markers.owner_id`), иначе
тип, показанный на трёх картах, выглядел бы одной записью. Холст строки — ближайший `space` или
мнемосхема через `CanvasResolver.ResolveForAsync` (пакетно, по уровню за запрос), имя — `markers.name`.
Группа: `device_diagrams`, если id диаграммы есть в `device_diagram`, иначе `diagrams`.

**Контракт:**

- proto: `rpc GetDiagramTypeUsage(GetDiagramTypeUsageRequest) returns (DiagramTypeUsage)`,
  запрос `{ type_id }` — `caller` не нужен: список не фильтруется по правам (см. ниже);
- `DiagramTypeUsage { repeated DiagramTypeUsageEntry diagrams; repeated DiagramTypeUsageEntry
  device_diagrams; }`;
- `DiagramTypeUsageEntry { diagram_id; name; canvas_id; canvas_name; is_representation;
  source_kind; source_id; }` — `diagram_id` это то, что открывается: реплика открывает диаграмму,
  которую показывает, со своими хлебными крошками (так уже работает `GetDiagramContent`);
- реализация — `Grpc/DiagramTypeUsage.cs` (статический помощник, рядом с `DiagramRepresentations`),
  вызов из `MapServiceImpl.Reads.cs`;
- права: строки **не** фильтруются по `View` — словарь типов глобальный, и фильтр сделал бы список
  пустым у того, кому удаление всё равно откажут. Но строки называют диаграммы и холсты, поэтому
  ручка живёт на админской поверхности (`AdminOnly`): объясняет она отказ, который получает только
  администратор. Авторитет — отказ, а не список (так и записано в proto и в контроллере);
- REST: `POST /api/map/admin/diagram-types/usage` в `MapAdminController`; клиент —
  `mapApi.getDiagramTypeUsage(typeId)`. Что ручка отвечается только с админской поверхности,
  закрепляет `MapDiagramTypeUsageAccessTests`.

**GUI (`MapDiagramTypesDialog`):** в раскрытом аккордеоне сохранённого типа — блок «Where this type is
used»: счётчик мест, две группы (обычные диаграммы, диаграммы устройств), пустой список говорит
«ничего не использует, тип можно удалить». Клик по строке открывает диаграмму через **новый**
`DiagramViewer.onOpenCanvas`: строка стоит на чужом холсте, и `onOpen` хоста такой узел не находит
(в `DiagramElementEditor` обработчика нет — там блок только читается). Список перечитывается при
каждом раскрытии типа, так что диаграмма, сделанная в другом окне, не прячется за старым ответом.
Кнопка удаления гаснет, пока список непуст **или ещё не прочитан**; не удавшееся чтение показывает
предупреждение с повтором, а не «ничего не использует». Отказ сервера при этом остаётся
обязательным. Строки — в `i18n/locales/{en,es}/translation.json`.

**Тесты:** `DiagramTypeUsageTests` (in-memory) — обычная диаграмма с репликой на другом холсте
(две строки, у реплики `is_representation`, холсты названы), привязка устройства попадает в
`device_diagrams` и не дублируется в `diagrams`, диаграмма другого типа не попадает, неиспользуемый
тип даёт пустой ответ.

**Порядок работы:** защита (шаги 1–5) отдельным коммитом, список использований — следующим; список не
должен быть условием корректности удаления.

## Что не входит

- Гонка «проверили — удалили»: у обеих соседних проверок та же экспозиция, локи — тема
  [24-map-lock-audit](../future_plans/24-map-lock-audit.md).
- Каскадное снятие типа.
- Удаление nullable-колонки `device_diagram.diagram_type_id` из схемы.
- Общий механизм «защищённое удаление» и универсальная проверка зависимостей: три проверки живут в
  двух конкретных сервисах и защищают одну модель, четвёртого потребителя нет — абстракция тут не
  окупается.

## Проверка

```
dotnet test dotnet/VmsMap.Tests/VmsMap.Tests.csproj --filter "FullyQualifiedName~DiagramTypeDeleteTests|FullyQualifiedName~MapDiagramWritePostgresTests|FullyQualifiedName~DemoSeeder"
```

Postgres-тесты идут, только если задан `VMS_MAP_TEST_DSN`; без него они молча возвращаются — это
надо сказать в отчёте, чтобы «зелёный» прогон не выдавал себя за проверенный каскад.

## Состояние на 2026-11-27

- Шаги 1–5 сделаны: `f14ebe59` (отказ в `DeleteAsync` + `InvalidArgument` у RPC), `a3bf3be7`
  (Postgres-тест границ каскада). Стражи — `DiagramTypeDeleteTests`, чтение — `DiagramTypeUsageTests`.
- Список использований: контракт, чтение, REST и блок в окне — `1ce462bb`; открытие строки —
  `667418cb`.
- Живая проверка на поднятом стеке с демо `umd-datacenter`:
  - «Camera rear panel» — 5 мест: одна обычная диаграмма (`spare` на `diag2`) и четыре у привязок
    (`cam1`/`cam2`; каноническая на `plan1`, реплика на `diag2` с пометкой «replica»); кнопка
    удаления погашена;
  - клик по строке открывает `diag2 / spare` со своими хлебными крошками;
  - `DELETE` используемого типа — 400 и имя диаграммы в теле; свободный тип создан, показан пустым
    и удалён.
- Найдено живой проверкой и исправлено: `onOpen` хоста не открывает холст, которого нет в текущем
  дереве, — добавлен `onOpenCanvas` (`667418cb`).
- Прогоны: `VmsMap.Tests` 587/587 с `VMS_MAP_TEST_DSN` на `ta_map_test`; `web_srv.Tests` 37/37;
  `tsc -b` и eslint — чисто.
- Стек оставлен поднятым (docker-инфраструктура, сервисы через `dev-up.sh`, Vite на 5173).

### Ревью 2026-11-27: доступ к списку и состояния загрузки

- **Доступ.** Сначала ручка стояла на обычном читающем контроллере без `caller` — то есть любой
  аутентифицированный пользователь получал имена диаграмм и холстов в обход прав. Перенесена в
  `MapAdminController` (`POST api/map/admin/diagram-types/usage`, `AdminOnly`): список объясняет
  отказ, а отказ получает только администратор. Права на объекты при этом не ослаблены и не
  продублированы — фильтр по `View` по-прежнему не нужен, потому что у администратора
  `RightsService` пропускает всё. Страховка — `MapDiagramTypeUsageAccessTests`: маршрут обязан
  объявляться на контроллере с `AdminOnly` и больше нигде.
- **Загрузка.** Кэш по типу на всё время жизни окна убран: список читается заново при каждом
  раскрытии аккордеона, поэтому «свободен» из прошлого открытия не переживает создание диаграммы в
  другом окне. Кнопка удаления гаснет, пока ответ не пришёл; ошибка чтения — предупреждение с
  кнопкой повтора, а не `usageNone`. Тесты GUI не заведены: поведение проверяется живой проверкой.
- **После второго ревью.** Тест доступа дополнительно проверяет, что ни на действии, ни на
  контроллере нет `[AllowAnonymous]` (сегодня нет; страховка от будущего), а кнопка удаления гаснет
  и на время операции (`busy`), чтобы сохранение и удаление не шли одновременно.
- **Живая проверка после merge (`beb18171`), 2026-11-27.** Пользователь `usage-check` без ролей:
  `POST /api/map/admin/diagram-types/usage` — 403; тот же токен на `POST /api/map/diagrams/types` —
  200; старый `POST /api/map/diagrams/types/usage` — 405 с `Allow: GET, HEAD` (API-маршрута нет,
  отвечает SPA-фоллбэк, так что 404 в этом приложении не бывает). Админ — 200 и те же 5 мест.
  GUI (Playwright, 17 проверок): «Used in 5 places» и погашенная кнопка; повторное раскрытие шлёт
  новый запрос (1 → 2); оборванный запрос — предупреждение с Retry, а не «nothing uses»; Retry
  возвращает список; строка `spare` открывает `diag2 / spare`; новый тип сохраняется, читается
  пустым, удаляется; после удаления строки нет.
