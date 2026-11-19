# Приём тревоги и пропажа части устройства

> Ветка `feature/alarm-acknowledgement` от `master` (`b9c8234b`).
>
> Исходник — `docs/future_plans/21-alarm-acknowledgement.md`. Когда работа начинается, этот файл
> и его строка в `docs/future_plans/README.md` удаляются.
>
> **Результат:** «принять» видно подписью в списке тревог и такой же подписанной кнопкой в панели
> объекта и в деталях события; причина отказа объясняется словами; принятие на охране разрешено,
> но требует комментария; пропажа части больше не закрывает тревогу сама собой — часть
> помечается отсутствующей, а решает оператор; вернувшаяся часть либо продолжает ту же тревогу,
> либо поднимает новую, если прежнюю закрыли.

## Сделано

- ☑ **VmsOnvif: `PartsForgotten` удалён.** `ForgottenParts`, событие `PartsForgotten` и его
  публикатор в `DeviceEventPublisher` удалены; `ForgetAsync` только бросает подписку и
  запомненное состояние, снимок частей не публикуется. Два теста заменены на «забывание не
  объявляет части». Проверено: `dotnet build dotnet/ta_vms.sln` — 0/0, `VmsOnvif.Tests` — 35.
- ☑ **VmsMap: пропажа части.** Reconciler больше не трогает строку камеры, на отсутствии части
  снимает контактное состояние и оставляет тревогу открытой (курсор принимается и сдвигается);
  `CloseForDeviceGoneAsync` удалён. `AlarmStore.CloseAsync` разрешает закрыть активную тревогу
  отсутствующей части с обязательным комментарием и снимает `ALARM` (вызов
  `SyncObjectAlarmStateAsync`). Семь новых тестов в `DeviceEventTests`, три в `AlarmStoreTests`
  вместо удалённого. Проверено: `dotnet build dotnet/ta_vms.sln` — 0/0, `VmsMap.Tests` — 400.
- ☑ **VmsMap: принятие на охране с комментарием.** `AcknowledgeAsync` отказывает только при
  `condition_active && armed` без комментария; массовое принятие зоны отдаёт
  `comment_required` вместо `armed`. Тесты: `AlarmStoreTests` (оба исхода), новый
  `AcceptingAZoneOnGuardRequiresAComment`. Проверено: `VmsMap.Tests` — 401.
- ☑ **proto и web_srv: `GetAlarm` и три поля.** `ack_blocked_reason`, `ack_requires_comment`,
  `device_absent` в `Alarm`; `rpc GetAlarm` и `GET api/map/alarms/{id}`; `can_ack` больше не
  зависит от охраны; `MetadataAsync` отдаёт `present`/`part_kind` страницы одним запросом. Тесты:
  `GetAlarmAnswersTheRowThePageHas`, `AGonePartIsReportedAbsentAndClosesOnlyWithAComment`,
  обновлённые проверки `can_ack`. Проверено: `dotnet build` — 0/0, `VmsMap.Tests` — 403,
  `web_srv.Tests` — 20.
- ☑ **Фронтенд: подписанная кнопка и общий диалог в трёх местах.** `alarms/AlarmAction.tsx`
  (`AlarmActionButton`, `AlarmHandleDialog`), чистая `alarms/alarmActions.ts` (+тест); подпись у
  действия строки в `MapObjectList`; секция тревог в `ObjectPanel` (камера — свои и датчиков);
  кнопка в деталях события по `alarm_id`; зона спрашивает комментарий, когда он обязателен;
  локали en/es. Проверено: `npm run build`, `npm run lint`, `npm test` — 93.
- ☑ **Документация.** `docs/maps/states-and-alarms.md` — правило пропажи части, принятие на
  охране с комментарием и три места, где оно показано; `docs/future_plans/21-alarm-acknowledgement.md`
  и его строка в индексе удалены.
- ☑ **Живая проверка (2026-11-19).** Dev-стек (`scripts/dev-up.sh`,
  `DEVICE_DISCOVERY_INTERVAL_SECONDS=30`), демо-мир `umd-datacenter`, OnvifSim Maryland из
  исходников на 8090/8101–8105 (Docker-образ не пересобирался: базовые слои не скачивались).
  Проверено через API и браузер (Playwright):
  - датчик `input` поднят помещением `plan1` (эффективно `armed` через пространство);
  - реле `loop` → тревога `active`; принятие на охране без комментария — **409** («a comment is
    required»), с комментарием — `acknowledged`; возврат контакта — `closed`/`returned_to_normal`,
    `closedBy=admin`;
  - новая тревога, вход убран из I/O камеры — после обхода состояние датчика **пропало**, тревога
    **осталась открытой** с `deviceAbsent=true`; закрытие без комментария — **409**, с
    комментарием — `closed`/`other`, `ALARM` снят;
  - вход возвращён при замкнутом реле — состояние вернулось, поднялась **новая** тревога;
  - UI: подпись «Acknowledge» у строки списка, в панели части и в панели камеры (тревога датчика
    видна у камеры); диалог не даёт подтвердить на охране без комментария; кнопка есть в деталях
    `alarm_raised`.
  Стенд, Vite и OnvifSim остановлены; демо-мир возвращён (пространство `inherit`, камера
  `disarmed`, открытых тревог 0).

## Что установлено

- **«Принять» есть только в карте оператора, во вкладке Alarms.** `MapsOperatorPanel.tsx:535`
  отдаёт строке `MapObjectList` действие-иконку `CheckCircleOutlineIcon`; общей подписи у
  действий нет — ни у строки, ни в `ObjectPanel.tsx`, ни в `MapEventDetails`
  (`EventsJournal.tsx:375`). Причина отказа `!canAck` — только `alarms.cannotAcknowledge`
  («This alarm cannot be acknowledged now»), без объяснения.
- **Отказ объясним сервером, но не передаётся.** `MapServiceImpl.Alarms.cs:321` знает и право, и
  эффективную охрану, и статус; в ответе есть только `can_ack` (`proto/vms_map.proto:1019`).
  Браузер не может отличить «нет права» от «условие активно и узел на охране».
- **Принятие на охране запрещено.** `AlarmStore.cs:371-380` отказывает `Active`-тревоге с
  `condition_active=true`, если узел эффективно на охране. Это единственный отказ, который
  задуман как политика, а не как состояние строки.
- **Пропажа части закрывает тревоги без оператора.** `DeviceEventReconciler.cs:213-221` помечает
  `present=false` всё, чего нет в манифесте, и тут же зовёт `CloseForDeviceGoneAsync`
  (`:227-228`); `AlarmStore.cs:467` закрывает **все** открытые тревоги узла с
  `close_reason='device_gone'`, `closed_by='system'`. Никто ничего не принимал, условие в норму
  не вернулось — это единственный такой путь в системе.
- **Строка самой камеры тоже попадает под «пропажу».** `rows` (`DeviceEventReconciler.cs:110`)
  включает строку `device_node` с `part_kind='device'`, а `listed` (`:90`) содержит только
  цифровые входы и реле. Поэтому при **каждом** успешном снимке камера помечается отсутствующей, и
  первая же ревизия закрывает как `device_gone` её тревоги — retention и subscription. Это тихая
  потеря живой тревоги записи.
- **`PartsForgotten` — второй, независимый способ сказать «части нет».** `VmsOnvif` шлёт снимок с
  `present=false` на каждый такой манифест (`DeviceIoStateService.cs:433`,
  `DeviceEventPublisher.cs:91`). Он срабатывает не только когда камера перестала отдавать I/O, но и
  **на изменение конфигурации камеры**: `CameraConfigSubscriber.cs:100` зовёт
  `ForgetAsync(cameraId)` с `announce: true`. Правка адреса или пароля камеры объявляет её части
  пропавшими.
- **Состояние отсутствующей части остаётся живым.** При `present=false` состояние
  `SENSOR_OPEN`/`SENSOR_CLOSED` не снимается; `ObjectPanel` и `StatesTable` читают его как
  обычное, а `present`/`reachable` вообще никуда наружу не отдаются.
- **Возврат части уже поднимает новую тревогу.** `DeviceEventReconciler.cs:172-177` добавляет
  перечисленную часть в `toReconcile`, `SensorAlarmService.ReconcileAsync` при живом контакте и
  охране зовёт `RaiseAsync`, а он создаёт новую строку, потому что прежняя закрыта (открытой по
  `(object_id, dedup_key)` нет). Если прежняя не закрыта, `ReconcileAsync` видит открытую
  `condition_active=true` и ничего не делает — это и нужно закрепить тестами.

## Решения

### 1. «Принять» видно и объяснено

- **Кнопка с подписью, одна на все три места.** Общий компонент
  `components/alarms/AlarmAction.tsx`: `AlarmAcknowledgeButton` (иконка + подпись
  `alarms.acknowledge`, `disabled` по `ack_blocked_reason`, `Tooltip` с причиной) и
  `AlarmHandleDialog` — диалог комментария/причины, переехавший из `MapsOperatorPanel.tsx` без
  изменения поведения (включая «returned → Close»). Им пользуются:
  - строка списка тревог — `MapObjectList` получает у действия необязательную подпись
    (`MapObjectItem.action.label`), и «✓» становится «✓ Принять»;
  - **панель объекта** — секция `Alarms` для выбранного узла. Для камеры это все её тревоги
    (`alarm.cameraId === marker.cameraId`, то есть и тревоги её датчиков — наблюдаемый случай
    «камера в красном кружке, а тревога на дочернем узле»); для части — только её собственная
    (`alarm.objectId === marker.ownerId ?? nodeId`); для остальных — по `objectId`. Данные —
    уже загруженные страницы `s.alarms` (карта оператора их опрашивает каждые 5 с);
  - **детали события** — если у события есть свойство `alarm_id` (`alarm_raised`,
    `alarm_acknowledged`, `alarm_returned`, `alarm_closed`), рядом с «Open on the map» стоит
    та же кнопка. Строку тревоги детали читают новым `GetAlarm(id)` — журнал не держит
    страницу тревог.
- **Причина отказа — с сервера.** В `Alarm` добавляются:
  - `string ack_blocked_reason = 25`: `""` — действие доступно; `"closed"` — тревога закрыта;
    `"no_right"` — нет права `acknowledge`;
  - `bool ack_requires_comment = 26`: условие активно и узел эффективно на охране, поэтому
    принятие требует комментария;
  - `bool device_absent = 27`: узла, на котором стоит тревога, нет в последнем успешном
    манифесте устройства (только часть, не камера).
- Подписи (en/es): «Тревога закрыта», «Нет права принимать тревоги на этом узле», «Условие
  активно, а узел на охране: нужен комментарий», «Устройство больше не отчитывается об этой
  части».

### 2. Принятие на охране разрешено — с обязательным комментарием

Политика меняется (решение пользователя 2026-11-19):

- `AlarmStore.AcknowledgeAsync` больше не отказывает по одной охране. Отказ остаётся только
  один: `condition_active && armed && комментарий пуст` — «условие активно и узел на охране;
  комментарий обязателен». Пустой комментарий на снятом с охраны узле по-прежнему допустим.
- Принятие не снимает `ALARM` и не закрывает тревогу, как и раньше: `acknowledged` — это
  отметка оператора, а не «пусть пройдёт».
- Массовое принятие зоны (`AcknowledgeSubtreeAlarms`) передаёт тот же комментарий; тревога на
  охране без комментария попадает в `rejected` с причиной `comment_required`, остальные
  принимаются. Отдельная причина `armed` из ответа исчезает.
- UI: кнопка активна, когда `ack_blocked_reason` пуст. Если `ack_requires_comment`, диалог
  требует комментарий (поле с `*`, кнопка подтверждения неактивна без текста). «Принять всё» у
  зоны открывает тот же диалог: комментарий обязателен, если хотя бы одна тревога группы его
  требует.

### 3. Пропажа части: система не закрывает, решает оператор

Политика (решение пользователя 2026-11-19): **пропажа части — неисправность, а не повод
закрывать тревогу**. Открытая тревога остаётся открытой, пока её не закроет оператор или пока
условие не вернётся в норму.

**В `DeviceEventReconciler`:**

- строка камеры (`part_kind='device'`) исключается из прохода «чего нет в манифесте»: камера
  ответила — она есть. Это чинит ложные `device_gone` на retention/subscription-тревогах.
- для части, которой нет в успешном снимке (или она перечислена с `present=false`), проверяется
  `DeviceCursor.Accept` (поздний снимок от старого процесса не считается), курсор сдвигается,
  ставится `present=false`, и снимаются `SENSOR_OPEN`/`SENSOR_CLOSED` (или
  `RELAY_ACTIVE`/`RELAY_INACTIVE`) через `ObjectStateWriter.ChangeAsync`. `ALARM` и тревога
  остаются.
- **`CloseForDeviceGoneAsync` больше не вызывается ниоткуда и удаляется.** `device_gone`
  остаётся в строке истории (старые записи читаются) и в CHECK-ограничении, но новых не пишет.
- схема БД не меняется: `present` и есть отметка «последний успешный снимок сказал: части нет».

**`PartsForgotten` убирается целиком.** Каждый успешный обход и так публикует полный манифест
камеры — это единственное утверждение, которому позволено говорить «части нет» (так и написано в
`DeviceIoDiscoveryService.cs:23`). Второй способ хуже: он срабатывает на смену конфигурации
(`CameraConfigSubscriber.cs:100`) и заставляет UI на минуту показать часть пропавшей. Удаляются
`PartsForgotten`, `ForgottenParts`, `OnPartsForgotten` и ветка `announce` в
`DeviceIoStateService.ForgetAsync`: забыть — значит только бросить подписку, а не объявить части
отсутствующими.

**Закрытие оператором.** `AlarmStore.CloseAsync` сейчас отказывает при `condition_active=true`.
Добавляется одно исключение: закрыть активную тревогу можно, если её узел — часть, которой нет в
последнем манифесте (`device_node.present=false`, `part_kind != 'device'`), и комментарий не пуст.
Это и есть «оператор разобрался с неисправностью»: причина `other` (или `false_alarm`), в журнале
`alarm_closed` с комментарием. Для присутствующей части отказ сохраняется — активное условие
по-прежнему нельзя погасить вручную.

**Возврат части:**

- тревога ещё открыта (`active` или `acknowledged`) и контакт снова живой → `ReconcileAsync`
  видит открытую `condition_active=true` и не создаёт вторую; состояние снова `SENSOR_OPEN`;
- оператор закрыл её → `OpenAlarmAsync` пуст, `RaiseAsync` поднимает новую тревогу (на охране);
- контакт вернулся в норму → `NormalAsync` переводит `active` в `returned`, а `acknowledged`
  закрывает.

### 4. За объёмом

- Модель неисправности устройства (`FAULT`/`TROUBLE`, тревога неисправности, защёлка) — тема
  [13-fire-alarm-integration](../../docs/future_plans/13-fire-alarm-integration.md).
- Уведомления о тревоге (`12-event-routing-followups`).
- Отдельная кнопка «Закрыть» в строке списка: закрытие остаётся в диалоге.
- Показ `reachable=false` в UI (поведение не меняется).
- Retention/subscription-тревоги: их политика не трогается; исправляется только ложное
  автозакрытие при снимке камеры.

## Изменения

### A. VmsMap

- `Adapter/DeviceEventReconciler.cs` — строка камеры вне прохода отсутствия; снятие контактного
  состояния на отсутствии; удаление вызова `CloseForDeviceGoneAsync`.
- `Adapter/AlarmStore.cs` — принятие на охране с обязательным комментарием; закрытие активной
  тревоги отсутствующей части с комментарием; удаление `CloseForDeviceGoneAsync`.
- `Grpc/MapServiceImpl.Alarms.cs` — `GetAlarm`; `ack_blocked_reason`, `ack_requires_comment`,
  `device_absent` в `ToAlarmAsync`; причина `comment_required` в `AcknowledgeSubtreeAlarms`.
- `Db/Services/CameraIdLookup`/`MetadataAsync` — `present` и `part_kind` для страницы тревог
  одним запросом (без чтения на строку).

### B. proto и web_srv

- `proto/vms_map.proto` — `ack_blocked_reason = 25`, `ack_requires_comment = 26`,
  `device_absent = 27` в `Alarm`; `GetAlarmRequest`, `rpc GetAlarm`.
- `web_srv.Server/Controllers/AlarmsController.cs` — `GET api/map/alarms/{id}`.
- `web_srv.client/src/dto/mapDtos.ts` — три новых поля.
- `web_srv.client/src/api/mapApi.ts` — `getAlarm(id)`.

### C. Фронтенд

- Новые `src/components/alarms/AlarmAction.tsx` (кнопка, диалог, тексты причин) и чистая
  `src/components/alarms/alarmActions.ts` (выбор тревог узла, требуется ли комментарий, можно ли
  закрыть) + тест.
- `src/components/maps/MapObjectList.tsx` — подпись у действия строки (`Button` вместо
  `IconButton`, когда подпись есть).
- `src/components/maps/MapsOperatorPanel.tsx` — диалог и кнопка из общего компонента; действие
  строки получает `label`; «Принять всё» у зоны спрашивает комментарий, когда он обязателен.
- `src/components/objects/ObjectPanel.tsx` — секция `Alarms` с кнопкой и пометкой «часть не
  отчитывается».
- `src/components/events/EventsJournal.tsx` — `EventAlarmAction` по `alarm_id`.
- `src/i18n/locales/en/translation.json`, `src/i18n/locales/es/translation.json` — новые
  подписи.

### D. VmsOnvif

- `DeviceIoStateService.cs` — удалить `PartsForgotten`, `ForgottenParts` и `announce`;
  `ForgetAsync` только бросает подписку и запомненное состояние.
- `DeviceEventPublisher.cs` — удалить подписку и `OnPartsForgotten`.
- `CameraConfigSubscriber.cs` — вызов остаётся, но теперь ничего не объявляет (комментарий
  поправить).
- `VmsOnvif.Tests/DeviceIoStateServiceTests.cs` — тесты `PartsForgotten` заменяются тестом
  «забывание бросает подписку и состояние, снимок не публикуется».

### E. Документация

- `docs/maps/states-and-alarms.md` — новое правило принятия (на охране можно, с комментарием) и
  пропажи части (система не закрывает; закрывает оператор).
- Удаление `docs/future_plans/21-alarm-acknowledgement.md` и его строки в
  `docs/future_plans/README.md`.
- Этот план — по ходу работы отметки «сделано» и чем проверено.

## Тесты

**VmsMap.Tests** (`DeviceEventTests`, `AlarmStoreTests`):

- снимок без части: `present=false`, `SENSOR_OPEN` снят, тревога остаётся `active`, `ALARM` на
  узле;
- повторный снимок без части: тревога всё ещё открыта (автозакрытия нет);
- `reachable=false` не считается отсутствием;
- поздний снимок от старого epoch не помечает часть отсутствующей;
- снимок, перечисляющий часть снова, возвращает живое состояние, а открытая тревога не
  дублируется;
- снимок камеры не закрывает её retention/subscription-тревогу (строка камеры вне прохода);
- принятие `active`-тревоги на охране без комментария — отказ; с комментарием — `acknowledged`,
  `ALARM` остаётся;
- закрытие активной тревоги присутствующей части — отказ; отсутствующей (`present=false`) без
  комментария — отказ, с комментарием — `closed`/`other`, `ALARM` снят;
- после закрытия возврат части с живым контактом поднимает новую тревогу.

**VmsOnvif.Tests** — забывание камеры не публикует снимок частей; смена конфигурации не
объявляет части отсутствующими.

**web_srv.Tests** — видимость `GetAlarm`; `409` у принятия на охране без комментария (через
существующий фильтр).

**Фронтенд** — `npm test`: выбор тревог узла (камера — свои и части, часть — свои), обязательность
комментария, доступность закрытия, подпись действия. `npm run build`, `npm run lint`.

**Сборка** — `dotnet build dotnet/ta_vms.sln`, `dotnet test` по `VmsMap.Tests`, `VmsOnvif.Tests`,
`web_srv.Tests` и всем затронутым.

## Живая проверка

Стенд: `scripts/dev-up.sh` (или F5) + `tests/onvifsim/onvifsim_lab.py up` (камера `sim-master`,
датчик `input`). `DEVICE_DISCOVERY_INTERVAL_SECONDS=20` у `vmsonvif`, чтобы не ждать пять минут.

1. Датчик в помещении; помещение на охране (`PUT api/control/objects/{id}/arming`).
2. Замкнуть контакт (`PUT /api/cameras/{id}/io` в симуляторе) → тревога `active`, `ALARM` в
   дереве и на карте.
3. Принять на охране: кнопка активна, диалог требует комментарий; без текста подтвердить нельзя,
   с текстом — `acknowledged`, `ALARM` остаётся.
4. Вернуть контакт в норму → `closed`/`returned_to_normal` (тревога была принята).
5. Поднять тревогу снова, **не** принимать; убрать вход из I/O камеры. Дождаться снимка:
   состояние датчика пропало, тревога **осталась открытой**; в панели видно «часть не
   отчитывается», кнопка «Закрыть» доступна.
6. Закрыть с комментарием → `closed`/`other`, `ALARM` снят.
7. Вернуть вход с тем же токеном в сработавшем положении → `SENSOR_OPEN` вернулся, поднялась
   новая тревога.
8. Повторить п. 5, но вернуть вход, **не** закрывая тревогу: состояние вернулось, второй тревоги
   нет.
9. В UI: у строки списка подпись «Принять»; у панели камеры — секция тревог её датчика; в деталях
   `alarm_raised` — та же кнопка.
10. Остановить стенд (`scripts/dev-down.sh`, `onvifsim_lab.py down`), проверить `dev_stack.py
    status` и порт 5173; ничего не оставлять запущенным.

## Порядок работ (по коммитам)

1. **VmsOnvif: убрать `PartsForgotten`.** Код, тесты, комментарии. Отдельный коммит — он не
   зависит от VmsMap.
2. **VmsMap: пропажа части.** Reconciler (строка камеры, снятие состояния, без автозакрытия),
   удаление `CloseForDeviceGoneAsync`, закрытие оператором, тесты.
3. **VmsMap: принятие на охране с комментарием.** `AcknowledgeAsync`,
   `AcknowledgeSubtreeAlarms`, тесты.
4. **proto + web_srv: `GetAlarm` и три поля `Alarm`.** Тесты.
5. **Фронтенд: общая кнопка и диалог, три места, локали, тесты.**
6. **Документация и удаление future plan.** Отметки в плане.

Push и merge в `master` — только по просьбе.

## Что остаётся за объёмом

- Модель неисправности и тревога неисправности (п. 4).
- Уведомления.
- `reachable` в UI.
- Отдельное действие «закрыть» в строке списка.
- Retention/subscription-политика.
