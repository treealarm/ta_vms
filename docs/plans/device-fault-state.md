# Неисправность части: `present` и тревога `source='fault'`

> Ветка `feature/device-fault-state` от `master` (`5b714deb`).
>
> Исходник — `docs/future_plans/13-fire-alarm-integration.md`, только те части, которые касаются
> неисправности устройства и защёлки. Остальное (адаптер панели, протоколы, зоны/шлейфы, пожарные
> состояния и приоритеты) остаётся в future plan и в этот объём не входит.
>
> **Результат:** отсутствующая часть видна оператору независимо от того, была ли на ней тревога
> условия. `device_node.present` остаётся единственной истиной о наличии; автоматическая тревога
> `source='fault'` использует существующий `AlarmStore`, защёлкивается на возврате и сбрасывается
> оператором через существующее закрытие. `FAULT` в словаре состояний, `ResetAlarm` и вторая
> сущность тревоги не вводятся.

## Сделано

- ☑ План переписан под вариант A: расширение существующего механизма тревог, без второго механизма
  состояний.
- ☑ Уточнены два пункта: `present` и fault-тревога пишутся в одной транзакции снимка под общим
  порядком блокировок, инвариант проверяется PostgreSQL-тестом гонки; `reason='reset'` — расширение
  контракта закрытия через proto/валидацию/CHECK/UI, а не уже существующая возможность.
- ☑ Схема и константы: `0004_device_fault.sql` (`source='fault'`, `close_reason='reset'`),
  `AlarmSource.Fault`, `AlarmCloseReason.Reset`; `dotnet build` — 0/0.
- ☑ Серверная часть: `DeviceFaultService`, `DeviceEventReconciler` (fault-сервис в транзакции
  снимка), `DeviceNodeService` (объединение частей манифеста и настроек), `AlarmStore`
  (`RaiseAsync`, `NormalFaultAsync`, `AcknowledgeAsync`, `CloseAsync`), DI. Проверено 2026-11-20:
  `VmsMap.Tests` — 417 с `VMS_MAP_TEST_DSN` на `ta_map_test`, в том числе последовательность
  `active → acknowledged → returned → active → returned → closed`, откат `present`+fault и гонка
  сброса со снимком (11 Postgres-тестов `AlarmStorePostgresTests`).
- ☑ proto и `present` наружу: `Marker`/`Figure` — `optional present`, `PresenceLookup`
  (каноническая часть и реплика через `owner_id`), все чтения маркеров/фигур, комментарии
  `source`/`close_reason`/`CloseAlarmRequest`. Проверено 2026-11-20: `VmsMap.Tests` — 419 с
  `VMS_MAP_TEST_DSN`, в том числе `present` у части и её реплики в `GetMarkersByIds`/`GetByIds`;
  `dotnet build` — 0/0.
- ☑ API/web_srv: `CloseAlarm` принимает `reset` только для fault и не для тревоги условия;
  `ToAlarmAsync` отдаёт `reset_required` у `returned`-fault и `ack_requires_comment=false`;
  `AcknowledgeSubtreeAlarms` пропускает fault; `AlarmCloseDto`/контроллер и proto-комментарий
  обновлены. Проверено 2026-11-20: `VmsMap.Tests` — 421 (с DSN), `web_srv.Tests` — 25, включая
  JSON `present`/`source`/`closeReason`.
- ☑ Фронтенд: `present` в `MapMarker`/`MapFigure`; `FAULT_COLOR` и янтарный `nodeStyle` при
  `present=false` (красный `ALARM` важнее); янтарные `PartStateDot`/`NodeStateChips`/`StatesTable`
  и предупреждение `ObjectPanel`; `CameraAlarm` `fault`/`faultReturned` и приоритет кольца;
  секция **Faults** отдельно от групп зон; `alarmActions` (`isFault`, `canResetFault`,
  `resetBlockedKey`), `AlarmAction` — «Reset fault» через `closeAlarm(..., 'reset', comment)`;
  локали en/es. Проверено 2026-11-20: `npm run build`, `npm run lint`, `npm test` — 109 тестов,
  включая `nodeStyle` и переходы `alarmActions`.

## Что установлено

- **Пропажа части уже обнаруживается.** `DeviceEventReconciler` помечает часть `present=false`,
  снимает `SENSOR_OPEN`/`SENSOR_CLOSED`/`RELAY_ACTIVE`/`RELAY_INACTIVE` и не трогает тревогу условия.
  Курсор `(epoch, seq, observed_at)` защищает от поздних снимков. `reachable=false` меняет только
  флаг доступности.
- **Но без тревоги условия пропажа невидима.** `present` читается только в
  `MapServiceImpl.Alarms.MetadataAsync`, чтобы поставить `device_absent` на **уже существующую**
  тревогу. В `Marker`/`Figure` поля нет; `PartStateDot` при пустом наборе состояний возвращает
  `null`; панель пишет «No states»; список тревог пуст. Если вход был `alarm_enabled=false`, снят с
  охраны, контакт был в норме или это реле — оператор не узнает о пропаже вообще.
- **Тревога условия — не запись о неисправности.** Если тревога условия была открыта, оператор
  видит её с `device_absent=true` и закрывает с комментарием по правилу 2026-11-19. После закрытия
  от неисправности не остаётся ничего, кроме невидимого `present=false`.
- **`device_node.present` — правильная истина.** Флаг уже транзакционно ведётся снимком; настройки
  части могут создать её узел до первого отчёта (`DeviceSettingsService.EnsureNodeAsync`), и тогда
  первый же успешный снимок без этой части помечает её отсутствующей. Отдельное состояние `FAULT`
  было бы вторым источником истины, который пришлось бы синхронизировать с `present`.
- **Три понятия не смешиваются:** наличие — `present`; связность — `reachable` и
  `CONNECTED`/`DISCONNECTED` камеры от VmsFs; тревога — `alarms` и `ALARM`. Эта задача меняет только
  первое и третье.

## Решения

### 1. `present` — единственный источник истины о наличии

- **Никакого `FAULT` в `object_state_descriptions`.** Состояние части, как и сейчас, остаётся пустым
  у отсутствующей части: контакт снят, потому что устройство о нём больше не говорит.
- **Наружу — `optional bool present` в `Marker` и `Figure`.** Поле заполняется только для части
  (`part_kind != 'device'`); у камеры, пространства и прочих узлов его нет. Для реплики значение
  берётся у владельца через `objects.owner_id`, тем же способом, каким `CameraIdLookup` и
  `OwnerView` уже разрешают камеру, состояния и тревоги реплики. Новый батчевый
  `PresenceLookup` (один запрос на страницу чтения, по образцу `CameraIdLookup`) обслуживает
  `MarkersAsync`, `GetByParent`, `GetByIds`, `GetObjProps`, `GetDiagramContent` и
  `ToProtoFiguresAsync`.
- **UI рисует неисправность по `present === false`, а не по тревоге.** Это важно: индикатор
  отсутствия не зависит от того, открыта ли fault-тревога, и остаётся видимым, даже если тревога
  недоступна, отфильтрована или ещё не поднята.
- **Отображение:** янтарная точка в `PartStateDot`, янтарный чип «Fault» в `NodeStateChips` и
  `StatesTable`, янтарная заливка реплики части в `nodeStyle`/`FiguresLayer`, янтарное
  предупреждение в `ObjectPanel`. Красный цвет тревоги условия важнее: если на части есть `ALARM`,
  узел красный, а неисправность видна чипом и в списке тревог.
- **Никакой агрегации по физическому дереву.** `present=false` не поднимает `alarm_states`, не
  увеличивает `children_alarms` и не зажигает пространство. Если когда-нибудь понадобится счётчик
  неисправностей по зданиям, это будет отдельный `children_faults`, считаемый через опорную
  реплику, а не переиспользование `children_alarms`.

### 2. Тревога `source='fault'` — задача оператора, а не второе состояние

- **Переиспользуем `alarms`.** Новый `source='fault'` в CHECK и `AlarmSource.Fault`; второй
  сущности нет. Страницы, видимость, права, принятие, закрытие и журнал — существующие.
- **Правило подъёма не зависит от конфигурации входа.** Для любой части (`digital_input` и
  `relay`) в принятом успешном снимке:
  - часть не перечислена или перечислена `present=false` → обеспечить открытую fault-тревогу;
  - часть перечислена `present=true` → если открытая fault-тревога ещё `condition_active`, перевести
    её в `returned`.
  `alarm_enabled`, `trigger_on`, охрана и состояние контакта не проверяются. Реле тоже даёт
  неисправность. `reachable=false` не даёт ничего: только успешный манифест может сказать, что части
  нет.
- **Первый снимок.** Узел части создаётся либо манифестом, либо настройками
  (`DeviceSettingsService.EnsureNodeAsync` уже делает это до первого отчёта). Чтобы это работало и
  для настроенных, но ни разу не перечисленных частей, `DeviceNodeService.EnsurePartsAsync` берёт
  объединение частей манифеста и `device_settings` (с тем же фильтром
  `digital_input`/`relay`). Часть, которой нет в первом успешном манифесте, получает
  `present=false` и fault-тревогу. Часть, о которой карта не знает ни из манифеста, ни из настроек,
  неисправной быть не может — это честная граница модели.
- **Стабильный dedup key.** `fault:{object_id}`, где `object_id` — канонический узел части из
  `device_node`, детерминированно выведенный из `(source_kind, source_id, part_kind, part_token)`.
  Ключ не строится из номера снимка, времени или случайного значения; частичный уникальный индекс
  `ux_alarms_open_dedup` гарантирует одну открытую fault-тревогу на узел. Если устройство меняет
  `part_token`, это по модели другая часть и другой узел: сопоставление переименованной части со
  старой физической — отдельная задача интеграции устройства, а не догадка в ключе.
- **Тревога не пишет `ALARM`.** `AlarmStore` для `fault` не зовёт `SetAlarmStateAsync` и не трогает
  `ALARM`/`NORM`; `SyncObjectAlarmStateAsync` для неё не вызывается. Поэтому fault-тревога не
  зажигает пространство и не попадает в `children_alarms`. Кольцо камеры янтарное — по строке
  тревоги, не по состоянию.
- **Severity `warning`, без `definition_id`.** Внешний `RaiseAlarm` по-прежнему принимает только
  `sensor`/`retention`/`subscription`; fault поднимает сам адаптер.

### 3. Жизненный цикл и защёлка

| Шаг | `status` | `condition_active` | Событие |
|---|---|---|---|
| пропажа | `active` | `true` | `alarm_raised` |
| принятие | `acknowledged` | `true` | `alarm_acknowledged` |
| возврат части | `returned` (из `active` и из `acknowledged`) | `false` | `alarm_returned` |
| пропажа снова до сброса | `active`, та же строка | `true` | `alarm_raised` |
| возврат снова | `returned`, та же строка | `false` | `alarm_returned` |
| сброс | `closed`, `close_reason='reset'` | `false` | `alarm_closed` |

- **Защёлка.** `NormalFaultAsync` не закрывает тревогу ни при каком статусе: `active` и
  `acknowledged` переходят в `returned`, `cleared_at` ставится, строка ждёт оператора. Это то же
  состояние `returned`, что и у тревоги условия, но действие другое — сброс, а не принятие.
- **Сброс использует существующее закрытие, но `reset` — новое значение контракта.** `CloseAlarm`
  с `reason='reset'`:
  - разрешён только при `condition_active=false`;
  - комментарий обязателен;
  - пишет `close_reason='reset'`, `closed_by`, `close_comment` и событие `alarm_closed`;
  - отдельного `alarm_reset` нет: `alarm_closed` с причиной `reset` и комментарием и есть запись.
  Это **не** уже существующая возможность: сегодня `CloseAlarm` принимает только
  `false_alarm`/`other`. `reset` проводится через proto (`CloseAlarmRequest.reason` и комментарий),
  валидацию `MapServiceImpl.CloseAlarm` (fault → `reset`, не-fault → `false_alarm`/`other`),
  `AlarmCloseReason.Reset` и CHECK `alarms_close_reason_check` (миграция), DTO/контроллер
  `web_srv`, union причин в `mapApi`/`alarmsSlice`/`AlarmAction`, тесты на всю цепочку и на отказ
  `reset` у не-fault. RPC не новый, но контракт расширяется сознательно и входит в коммит
  схемы/API.
- **Атомарность `present` и fault-тревоги.** Изменение `present` и переход fault-тревоги
  выполняются в **одной транзакции снимка**. `DeviceEventReconciler` после установки флагов
  `present`, ещё до `SaveChangesAsync`/commit, зовёт `DeviceFaultService.ApplyAsync`; тот через
  `AlarmStore` пишет тревогу и событие в тот же `VmsMapDbContext` и ту же транзакцию. `present=false`
  и `active`-fault (или `present=true` и `returned`/закрытая) коммитятся или откатываются вместе;
  состояния «флаг записан, тревоги нет» после успешного коммита не существует.
  - `AlarmStore.BeginAsync` видит открытую транзакцию снимка, не открывает вторую, но берёт те же
    `MapLocks` (`ta_map_camera_state`, затем `ta_map_alarm`); строки `device_node` уже удерживаются
    транзакцией снимка. Порядок один для всех путей: строки `device_node` снимка → camera-state →
    alarm → строка тревоги. `CloseAsync` оператора берёт camera-state → alarm → строку тревоги и
    `device_node` не пишет, поэтому цикла блокировок нет.
  - Для `fault` `RaiseAsync`/`NormalFaultAsync` не ходят в VmsCfg: `RaiseAsync` не ищет
    определение, `NormalFaultAsync` не читает список определений. Сетевой вызов не удерживается
    под блокировками карты. `NormalFaultAsync` — отдельный вход в `AlarmStore` с той же
    транзакцией и блокировками, что и `NormalAsync`, но без загрузки определений; `NormalAsync`
    остаётся для не-fault.
  - `DeviceFaultService` читает открытые fault-тревоги камеры одним запросом, но это только выбор,
    какой переход попытаться выполнить. `RaiseAsync`/`NormalFaultAsync`/`CloseAsync` перепроверяют
    строку под `FOR UPDATE`, поэтому устаревшее чтение не устанавливает неверное состояние.
  - `CloseAsync` оператора сериализуется со снимком на `ta_map_alarm` и строке тревоги: если снимок
    зафиксировался первым, сброс видит новое `condition_active` и отказывает или закрывает по нему;
    если первым зафиксировался сброс, снимок видит закрытую строку и создаёт новый fault для новой
    пропажи. Итоговый инвариант после обеих операций:
    - `present=false` ⇔ ровно одна открытая fault-тревога на часть с `condition_active=true`;
    - `present=true` ⇔ нет открытой fault с `condition_active=true` (`returned` или `closed`
      допустимы).
    Это и проверяет PostgreSQL-тест гонки, а не только факт блокировки строки.
  - Снятие контактных состояний и `SensorAlarmService` остаются после коммита: они не участвуют в
    инварианте `present`/fault и не должны удерживать транзакцию снимка на сетевом вызове охраны.
- **Принятие `returned`-неисправности запрещено.** `AcknowledgeAsync` для `returned`-fault
  отказывает с причиной `reset_required`: закрывает неисправность только сброс. `ack_blocked_reason`
  остаётся существующим полем; `can_ack` продолжает означать «у вызывающего есть право на этом
  узле», поэтому UI по `source/status` выбирает кнопку «Reset fault», а не «Acknowledge».
- **Fault и тревога условия независимы.** Ни принятие, ни сброс одной записи не меняют другую.
  Тревога условия остаётся открытой, её `device_absent` остаётся объяснением, закрывается с
  комментарием как сегодня. `AcknowledgeSubtreeAlarms` («принять всё» у зоны) пропускает
  `source='fault'`: неисправности — отдельная секция и отдельные действия.
- **История.** `alarm_raised` пишется и при создании, и при оживлении `returned`-строки; поэтому
  последовательность `active → acknowledged → returned → active → returned → closed` видна в журнале
  как `raised/acknowledged/returned/raised/returned/closed` с одним `alarm_id`. Само изменение
  `present` отдельного события не пишет; `present` и fault-тревога коммитятся одной транзакцией, а
  при откате следующий принятый снимок повторяет переход; `alarm_raised` — запись о пропаже.

### 4. Что видит оператор

- **Дерево.** У части — янтарная точка `PartStateDot` с подсказкой «Fault»; в `StatesTable` и
  `NodeStateChips` — янтарный чип. У камеры состояние части не показывается: неисправность видна в
  панели камеры через её тревоги.
- **Карта.** Реплика части, если размещена, янтарная при `present=false`; красная, если на ней же
  тревога условия. Кольцо камеры: красный пульс — живая тревога условия; бледно-красный — недавно
  закрытая; сплошной янтарный — живая fault-тревога; бледно-янтарный — `returned`-fault. Порядок:
  живая тревога условия > живая fault > fault без сброса > недавно закрытая тревога. Сброшенная
  fault кольца не даёт. Пространство не загорается.
- **Список тревог.** Отдельная секция **Faults** над группами зон: янтарная точка, источник
  «Fault», статус, подписанное действие. Группы зон строятся только по тревогам условия.
- **Панель.** У камеры — fault-строки её частей (через `cameraId`); у части — её `present=false` и
  её fault-тревога. Диалог объясняет «устройство больше не отчитывается об этой части» и
  «неисправность ещё есть», когда сброс недоступен.
- **Действия.** «Acknowledge» у активной неисправности (комментарий необязателен), «Reset fault» у
  вернувшейся (комментарий обязателен, вызов существующего `CloseAlarm` с `reason='reset'`).
  Доступность — с причиной: нет права, ещё отсутствует, уже закрыта.
- **Права.** И принятие, и сброс — `RightActions.Acknowledge` на узле. `control` не используется:
  сброс меняет учёт карты, а не командует устройством; сброс панели будет командой `control` за
  адаптером панели (за объёмом).
- **Реплики и агрегация.** `present` реплики резолвится через `owner_id`; fault-тревога несёт
  `camera_id` из `device_node`, поэтому панель камеры и `alarmsForNode` находят её без обхода
  физического дерева. `alarm_states` не меняется. Если появится требование показывать неисправность
  на пространстве, это будет отдельный счётчик через опорную реплику, а не `children_alarms`.

### 5. Возврат, удаление, недоступность, первая регистрация

- **Возврат.** Принятый успешный снимок с `present=true` снимает `present=false`; fault-тревога
  становится `returned`, но не закрывается; контактное состояние восстанавливается из снимка;
  тревога условия пересчитывается как сегодня (продолжается или возвращается/закрывается). Второй
  fault нет.
- **Повторная пропажа до сброса.** Та же `returned`-строка оживает (`RaiseAsync` уже умеет
  `returned → active`); второй открытой fault на узел не появляется. После сброса (закрыта) новая
  пропажа — новая строка.
- **Удаление камеры.** `RemoveCameraAsync` удаляет узел камеры, части, реплики, размещения,
  наблюдения и строки `device_node`; состояния и тревоги уходят каскадом вместе с узлами, как
  сегодня. История не сохраняется отдельно — это общее правило схемы: пересозданная камера получает
  новый id и ничего не наследует. Висящей fault нет.
- **Удаление части из конфигурации/манифеста.** Отдельного жизненного цикла части нет: часть,
  пропавшая из манифеста, остаётся узлом с `present=false` и fault-тревогой; оператор сбрасывает её,
  когда часть вернулась, либо администратор исправляет настройку. Удаления узла части по
  конфигурации в этом объёме нет: если настройка удалена, а часть так и не вернулась, узел и fault
  остаются (сброс по-прежнему требует возврата части). Это известное ограничение модели, а не
  забытый случай; жизненный цикл части — отдельная задача. Удаление камеры, в отличие от этого,
  уносит узел и fault каскадом.
- **Недоступность.** `reachable=false` не создаёт fault, не снимает её и не меняет `present`.
  Последний успешный манифест остаётся последним словом. `DISCONNECTED`/`CONNECTED` — отдельное
  состояние камеры от VmsFs; часть его не получает.
- **Первая регистрация.** Если часть настроена, её узел уже есть; первый успешный снимок без неё
  даёт `present=false` и fault. Если часть не настроена и ни разу не перечислялась, карта о ней не
  знает и неисправности не заводит.

### 6. За объёмом

- Адаптер пожарной панели и протоколы (Modbus/BACnet/OPC UA/SIA/вендорский API), зоны и шлейфы,
  команда сброса панели, cause-and-effect, EN 54/NFPA 72.
- Пожарные состояния (`FIRE_ALARM`, `FIRE_PREALARM`, `SUPERVISORY`, `FIRE_TEST`), пожарные
  приоритеты, план эвакуации, `device_class`, `arm_policy='always'`, `maintenance_bypass`.
- Настраиваемая защёлка в определениях тревог; `children_faults` и загорание пространств по
  неисправности; уведомления; `reachable` в UI; вторая сущность неисправности; жизненный цикл части
  при удалении из конфигурации.

## Изменения

### A. Схема

- Новый `dotnet/VmsMap/Grpc/migrations/0004_device_fault.sql`:
  - `ALTER TABLE alarms DROP CONSTRAINT alarms_source_check; ADD CONSTRAINT alarms_source_check
    CHECK (source IN ('sensor', 'retention', 'subscription', 'fault'));`
  - `ALTER TABLE alarms DROP CONSTRAINT alarms_close_reason_check; ADD CONSTRAINT
    alarms_close_reason_check CHECK (close_reason IN (…, 'reset'));`
  - строки словаря `FAULT` **нет** — состояние не вводится.
  - Имена CHECK проверить `\d alarms` перед написанием.
- `dotnet/VmsMap/Db/Models/States/DBAlarm.cs`: `AlarmSource.Fault = "fault"`,
  `AlarmCloseReason.Reset = "reset"`.

### B. VmsMap

- `Adapter/DeviceEventReconciler.cs`: после установки флагов `present` и **до**
  `SaveChangesAsync`/commit вызвать `DeviceFaultService.ApplyAsync` со всеми частями камеры (не
  только переходами), чтобы `present` и fault-тревога вошли в одну транзакцию; `ClearContactStateAsync`
  и `SensorAlarmService` остаются после коммита. Комментарии обновить.
- `Adapter/DeviceNodeService.cs`: `EnsurePartsAsync` создаёт узлы для объединения частей манифеста и
  `device_settings` (фильтр `digital_input`/`relay`), чтобы настроенная, но не отчитывающаяся часть
  была видна и могла получить fault.
- Новый `Adapter/DeviceFaultService.cs` — только тревога, без состояний:
  - `DedupKey(objectId) => $"fault:{objectId}"`;
  - `ApplyAsync(cameraId, parts, ct)`: один запрос открытых fault-тревог по камере; для
    отсутствующей части без открытой тревоги или с `returned` — `RaiseAsync`; для присутствующей
    части с открытой `condition_active=true` — `NormalFaultAsync`; вызывается внутри транзакции
    снимка, идемпотентен и сходится на следующем принятом снимке, если предыдущий откатился;
  - `RaiseAlarmSpec` с `Source: AlarmSource.Fault`, `Severity: AlarmSeverity.Warning`,
    `Trigger: "part_absent"`.
- `Adapter/AlarmStore.cs`:
  - `RaiseAsync`: для `fault` не искать определение в VmsCfg, не звать `SetAlarmStateAsync` (и при
    создании, и при оживлении `returned`), не писать warning об отсутствующем определении;
  - новый `NormalFaultAsync(objectId, dedupKey, ct)`: тот же вход в транзакцию и те же блокировки,
    что у `NormalAsync`, но без загрузки определений; `condition_active=false`, `cleared_at`,
    `returned` из `active` и `acknowledged`, `alarm_returned`, без авто-закрытия и без
    `SyncObjectAlarmStateAsync`. `NormalAsync` остаётся для не-fault;
  - `AcknowledgeAsync`: для `fault` не проверять охрану/комментарий; `returned`-fault отклонить с
    `reset_required`;
  - `CloseAsync`: для `fault` — отказ при `condition_active=true`; комментарий обязателен; причина
    `reset`; `closed_at`/`closed_by`/`close_comment`; `alarm_closed`; без
    `SyncObjectAlarmStateAsync`;
  - `WriteEventAsync` не меняется (кроме значений).
- `Db/Services/PresenceLookup.cs` (новый, по образцу `CameraIdLookup`): `present` для части и её
  реплики через `owner_id`; один запрос на чтение.
- `Grpc/MapServiceImpl.Reads.cs`: `optional present` в `ToMarker`/`ToProtoFiguresAsync` и все
  вызовы; `MarkersAsync`, `GetByParent`, `GetByIds`, `GetObjProps`, `GetDiagramContent`,
  `ToProtoFiguresAsync` получают `PresenceLookup`.
- `Program.cs`: `AddScoped<DeviceFaultService>()`.

### C. proto и web_srv

- `proto/vms_map.proto`:
  - `Marker`: `optional bool present = 9;`; `Figure`: `optional bool present = 14;` — только для
    части, у прочих узлов поля нет.
  - `Alarm.source` comment добавляет `"fault"`; `close_reason` — `"reset"`;
    `AlarmRejection.reason` — `"reset_required"`; `CloseAlarmRequest` — неисправность
    сбрасывается `reason='reset'` с комментарием и только когда часть вернулась.
  - Новых RPC и полей `Alarm` нет.
- `dotnet/VmsMap/Grpc/MapServiceImpl.Alarms.cs`:
  - `CloseAlarm`: допустимые причины `false_alarm`/`other`/`reset`; после чтения строки —
    `fault → reset`, не-fault → `false_alarm`/`other`; право `acknowledge` как сегодня;
  - `ToAlarmAsync`: для `returned`-fault `ack_blocked_reason="reset_required"`;
    `ack_requires_comment=false` для `fault`; `can_ack` остаётся «есть право и не закрыта»;
  - `AcknowledgeSubtreeAlarms`: пропускать `fault`.
- `dotnet/web_srv.Server/Controllers/AlarmsController.cs`: `AlarmCloseDto.Reason` принимает
  `reset`; пересылка без изменений. Это часть расширения контракта `reset`, а не уже существующая
  возможность.
- `dotnet/web_srv.Tests`: `present` в JSON маркера/фигуры; `close` с `reason='reset'` проходит
  proto → VmsMap → ответ; `reset` у не-fault отклоняется; fault при `condition_active` — 409.

### D. Фронтенд

- `src/dto/mapDtos.ts`: `MapMarker`/`MapFigure` — `present?: boolean`; `source` включает `"fault"`;
  `closeReason` — `"reset"`; `closeAlarm` reason union.
- `src/api/mapApi.ts`: `closeAlarm` принимает `'reset'`.
- `src/store/alarmsSlice.ts`: без нового thunk; `closeAlarm` reason union.
- `src/map/cameraStatus.ts`: `FAULT_COLOR = '#FFC107'` рядом с `ALARM_COLOR`; `src/map/nodeStyle.ts`
  и `src/map/markers.tsx` используют его. `nodeStyle(..., present?)` — `present === false` и не
  `alarming` → янтарный; `alarm.alarm` по-прежнему важнее. Все вызовы `nodeStyle` (в том числе
  `FiguresLayer`, `DiagramElement`, `placementColor` в `MapsOperatorPanel`) передают `present`
  фигуры/маркера.
- `src/map/markers.tsx`: `CameraAlarm = 'active' | 'recent' | 'fault' | 'faultReturned' | null`;
  янтарное кольцо для fault, красное для условия;
  `MapsOperatorPanel` делит `alarmRows` на fault/условие, `byCamera` учитывает порядок из решения 4,
  секция **Faults**, `alarmGroups` — только условия.
- `src/components/objects/PartStateDot.tsx`: `present?: boolean`; `false` → янтарная точка с
  подсказкой, даже если состояний нет.
- `src/components/objects/NodeStateChips.tsx`, `src/components/events/StatesTable.tsx`: чип
  «Fault» из `present === false`; «No states»/«—» не показывается, когда чип есть.
- `src/components/objects/ObjectPanel.tsx`: янтарное предупреждение для `present === false`; общая
  кнопка тревоги выбирает reset для `returned`-fault.
- `src/components/alarms/alarmActions.ts`: `isFault`, `canResetFault` (`source==='fault' &&
  status==='returned' && !conditionActive && canAck`), `closeNeedsComment` для fault; `canCloseAlarm`
  для fault не предлагает обычное закрытие.
- `src/components/alarms/AlarmAction.tsx`: кнопка «Reset fault» (`RestartAlt`) для `returned`-fault;
  диалог `'reset'` — комментарий обязателен, причин `false_alarm`/`other` нет, submit вызывает
  `closeAlarm(id, 'reset', comment)`; активная fault — обычное принятие с необязательным
  комментарием.
- `src/i18n/locales/en/translation.json`, `es`: `alarms.source.fault`, `alarms.reset`,
  `alarms.resetHint`, `alarms.blocked.resetRequired`, `alarms.faultStillPresent`,
  `alarms.handleFault`, `maps.faults`, `maps.node.fault`, `alarms.closeReason.reset`.

### E. Документация

- `docs/maps/states-and-alarms.md`: правило пропажи части — `present` как истина, `source='fault'`,
  защёлка, сброс через закрытие с `reset`, независимость от тревоги условия, `reachable=false` не
  неисправность, `present` на реплике через `owner_id`, отсутствие `ALARM`/агрегации.
- `docs/maps/control.md`: `present=false` читается в дереве/панели; сброс — действие тревоги
  (`acknowledge`), не `control`.
- `docs/future_plans/13-fire-alarm-integration.md`: сократить до оставшегося (адаптер панели,
  протоколы, зоны/шлейфы, пожарные состояния/приоритеты, cause-and-effect, `device_class`,
  настраиваемая защёлка); добавить строку, что неисправность части и защёлка fault сделаны, со
  ссылкой на этот план. Строку в `docs/future_plans/README.md` обновить.
- Этот план — отметки «сделано» по ходу, с тестами и живой проверкой.

## Тесты

**VmsMap.Tests:**

- `DeviceEventTests`:
  - отсутствующая часть: `present=false`, контакт снят, ровно одна fault-тревога `active`;
  - повторный снимок без части: второй тревоги нет;
  - реле: тоже fault;
  - настроенная, но не перечисленная часть в первом снимке: узел есть, `present=false`, fault;
  - `reachable=false`: ни fault, ни изменения `present`; поздний снимок ничего не меняет;
  - возврат: `present=true`, fault `returned`, не закрыта; тревога условия не задублирована;
  - повторная пропажа до сброса: та же строка снова `active`, новой нет;
  - удаление камеры: узла, `present` и fault нет.
- `AlarmStoreTests`:
  - последовательность `active → acknowledged → returned → active → returned → closed` с одним
    `alarm_id` и событиями `raised/acknowledged/returned/raised/returned/closed`;
  - fault не пишет `ALARM`; `alarm_states`/`children_alarms` не меняются;
  - принятие активной fault без комментария — `acknowledged`; принятие `returned` — отказ
    `reset_required`;
  - сброс активной fault — отказ; сброс `returned` без комментария — отказ; с комментарием —
    `closed`/`reset`; `CloseAlarm` не-fault с `reset` — отказ;
  - fault и sensor на одном узле независимы: закрытие одной не меняет другую;
  - `AcknowledgeSubtreeAlarms` пропускает fault.
- `AlarmStorePostgresTests`:
  - гонка сброса и нового снимка (блокировка строки): после обеих операций `present=false` ⇔
    ровно одна открытая fault на часть с `condition_active=true`; `present=true` ⇔ нет открытой
    fault с `condition_active=true`; закрытой строки с `condition_active=true` и двух открытых
    fault на узел нет. Проверяется именно согласованность `present` и тревоги, а не только факт
    блокировки;
  - атомарность: снимок, меняющий `present` и поднимающий/снимающий fault, откатывается целиком —
    сбой после записи `present` не оставляет флаг без тревоги и наоборот;
  - CHECK принимает `source='fault'`/`close_reason='reset'`; причина `reset` проходит через
    `CloseAlarm` (proto → VmsMap → БД), а у не-fault отклоняется.
- `AlarmAggregationTests`: fault-тревога не поднимает `alarm_states.alarm` и `children_alarms`.
- Чтения: `Marker.present`/`Figure.present` — `false` у отсутствующей части, нет у прочих узлов,
  `false` у реплики отсутствующей части через `owner_id`.

**web_srv.Tests:** `present` в JSON; `close` с `reason='reset'`; `reset` у не-fault отклоняется;
fault при `condition_active` — 409.

**Фронтенд:** `npm test` — `nodeStyle` (янтарный `present=false`, красный `ALARM` важнее),
`PartStateDot`, `alarmActions` (reset только у `returned`-fault, комментарий обязателен, обычное
закрытие не предлагается), `StatesTable`; `npm run build`, `npm run lint`.

**Сборка:** `dotnet build dotnet/ta_vms.sln`; `dotnet test` по `VmsMap.Tests`, `web_srv.Tests` и
затронутым проектам.

## Живая проверка

Стенд: `scripts/dev-up.sh` (или F5), `DEVICE_DISCOVERY_INTERVAL_SECONDS=20` у `vmsonvif`,
OnvifSim из `../OnvifSim` на 8090/8101–8105 (Docker-образ может не пересобраться; сборка
`-p:BuildEditor=false`), мир `maryland` через `onvifsim_lab.py up --world maryland`.

1. Датчик (`input`) в помещении; помещение на охране.
2. Замкнуть контакт → тревога условия `active`, `ALARM` в дереве и на карте.
3. Убрать вход из I/O камеры → `present=false`: янтарная точка/чип, янтарное кольцо камеры,
   fault-тревога `active`; тревога условия открыта с `deviceAbsent=true`.
4. Принять fault без комментария → `acknowledged`; `ALARM` не изменился.
5. Вернуть вход в сработавшем положении → `present=true`, fault `returned` (ждёт сброса), тревога
   условия продолжается, второго fault нет.
6. Сбросить fault без комментария → **409**; с комментарием → `closed`/`reset`, в журнале
   `alarm_returned` + `alarm_closed`.
7. Повторить п. 3, не сбрасывая: та же fault-строка снова `active`, новой нет; сброс при всё ещё
   отсутствующей части → **409**.
8. Проверить независимость индикатора: при `present=false` точка/чип видны и без открытой
   fault-тревоги (например, сразу после удаления входа, до поднятия тревоги, и в API
   `GET api/map/...` маркер отдаёт `present=false`).
9. Сделать камеру недоступной (остановить в симуляторе или закрыть порт) → `reachable=false`, ни
   fault, ни изменения `present`; если стенд не позволяет погасить одну камеру, это покрыто тестом и
   в отчёте сказано.
10. UI (Playwright): янтарные точка/чип, секция **Faults**, подпись «Reset fault», янтарное кольцо,
    пространство не загорается; тревога условия и fault — две независимые строки.
11. Удалить камеру из конфигурации → узел, `present` и fault исчезли.
12. Остановить стенд (`scripts/dev-down.sh`), Vite и OnvifSim (`onvifsim_lab.py down`); вернуть
    демо-мир (пространство `inherit`, камера `disarmed`, открытых тревог 0); проверить
    `dev_stack.py status` и порт 5173 — ничего не оставлять запущенным.

## Порядок работ (по коммитам)

0. **План.** Этот файл — отдельный коммит до кода.
1. **Схема и константы.** `0004_device_fault.sql` (`source='fault'`, `close_reason='reset'`),
   `AlarmSource.Fault`, `AlarmCloseReason.Reset`.
2. **VmsMap, серверный fault — без proto и фронтенда.** `DeviceNodeService` (объединение с
   настройками), `DeviceEventReconciler` (fault-сервис в одной транзакции с `present`),
   `DeviceFaultService`, `AlarmStore` (`RaiseAsync`, `NormalFaultAsync`, `AcknowledgeAsync`,
   `CloseAsync`). Тесты:
   - переходы `active → acknowledged → returned → active → returned → closed`;
   - атомарность `present`/fault: откат снимка уносит и флаг, и тревогу;
   - `AlarmStorePostgresTests`: гонка сброса и снимка, итоговый инвариант `present` ⇔ fault
     (`condition_active=true`), отсутствие двух открытых fault;
   - fault не пишет `ALARM`, не трогает `children_alarms`, независим от sensor-тревоги.
   **Точка подтверждения.** К proto и фронтенду не переходить, пока эти инварианты не проходят.
   Если тест находит окно — исправлять здесь, в серверной транзакции/блокировках, а не в UI.
3. **proto + VmsMap `present` наружу.** `Marker`/`Figure` `present`, `PresenceLookup`, чтения,
   тесты реплик; комментарии `source`/`close_reason`/`CloseAlarmRequest`.
4. **web_srv.** `present` в JSON, `CloseAlarm` с причиной `reset` (валидация и проброс),
   `ToAlarmAsync`, `AcknowledgeSubtreeAlarms`, тесты.
5. **Фронтенд.** `present`-индикатор, секция Faults, янтарное кольцо, кнопка «Reset fault» через
   `closeAlarm`, локали, тесты.
6. **Документация и сокращение future plan.** `states-and-alarms.md`, `control.md`, future plan,
   его строка в индексе, отметки в этом плане.
7. **Живая проверка.** Шаги выше; отметки в плане.

Push и merge в `master` — только по просьбе.

## Что остаётся за объёмом

- Адаптер пожарной панели и протоколы; зоны/шлейфы; пожарные состояния и приоритеты;
  cause-and-effect; EN 54/NFPA 72.
- `device_class`, `arm_policy='always'`, `maintenance_bypass`, настраиваемая защёлка в определениях
  тревог.
- `children_faults` и загорание пространств по неисправности; уведомления; `reachable` в UI.
- Вторая сущность неисправности и жизненный цикл части при удалении из конфигурации.
