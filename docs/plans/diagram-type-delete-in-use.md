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
- Две ссылки на тип живут без внешнего ключа:
  - `dbContext.Diagrams` (`DBDiagram.dgr_type`, `Guid?`) — диаграмма нарисована из типа;
  - `dbContext.DeviceDiagrams` (`DBDeviceDiagram.diagram_type_id`, `Guid?`) — привязка устройства
    держит диаграмму по шаблону.
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
