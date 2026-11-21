# Обход устройства или зоны с необязательным сроком (`bypassed`)

> Ветка `feature/zone-arming-bypass` от `master`. В задаче назван `4319af78`, но текущий `master` —
> `70ae0de9`: шесть коммитов впереди (предупреждение сборки Windows, URL x264), к охране отношения
> не имеют. Режу от текущего `master`.
>
> Исходник — `docs/future_plans/20-zone-arming.md`, пункт «`bypassed`».
>
> **Результат:** оператор видит, что устройство или зона исключены из охраняемой зоны, а не
> «просто сняты»; может задать срок, после которого узел сам вернётся в прежний режим; может
> снять с охраны сразу целую комнату одной операцией, не трогая каждый датчик.

## Контекст

M2.4i сделал охрану пространств и зон свойством `armed` на узле. Явный `Disarmed` на устройстве
или контейнере перекрывает зону: резолвер останавливается на собственной строке, тревог не
поднимает, флаг камеры остаётся `false`, уже открытая тревога не закрывается. Но подача одна на
два разных случая — «зону не ставили или сняли» и «зона на охране, а этот узел исключён». Кроме
того, исключение легко забыть: дыра в охраняемой зоне остаётся навсегда.

**Юскейс — операционный.** Работы в охраняемой зоне (окно на проветривании, дверь на ремонте,
погрузка): зона остаётся на охране, один узел исключён на время работ. Оператор должен видеть
исключение при передаче смены и при разборе тревоги, а по окончании срока узел должен вернуться
сам — иначе исключение забудут. Для «снять комнату целиком» действие делается на помещении, а не
на каждом датчике.

**Примеры.**
- Здание на охране, помещение снимается на два часа: одна операция на помещении
  (`disarmed`, `until = 18:00`, `restore_mode = inherit`). Датчики в `inherit` под помещением
  перестают поднимать тревоги; явно поставленные — остаются. В 18:00 помещение снова следует
  зданию; если окно всё ещё открыто, тревога поднимается сразу.
- Помещение само — верхний уровень охраны: снимается до 18:00 с `restore_mode = armed`; в 18:00
  снова встаёт на охрану, даже если родитель снят.

## Ответы на вопросы

1. **Что значит «bypassed» как подача.** Это не новый режим, а второй смысл существующего: у узла
   явный `disarmed`, а ближайшая явная строка контейнера над ним (для устройства — над опорной
   репликой) — `armed`. «Off guard» (серый) означает «никто не охраняет», «Bypassed» (янтарный) —
   «зона на охране, этот узел из неё исключён». Тревог исключённый узел не поднимает; уже
   открытая тревога остаётся, пока не уйдёт условие.
2. **Отдельный признак или UI-пометка.** Видимое состояние по-прежнему выводится из существующих
   строк, без четвёртого режима `arm_mode`: сервер вычисляет `bypassed` и заполняет
   `source_object_id` зоной. Но **срок — это хранимое намерение**, поэтому появляются
   `bypass_until` и `restore_mode` рядом с явным `disarmed` (в `device_object_settings` и
   `object_arming`). Это не второй источник истины: та же строка, что и решение; чек-констрейнт
   не даёт таймеру существовать без `disarmed`. Четвёртый режим по-прежнему отклонён.
   **Сам `Bypassed` не хранится и отдельным событием не журналируется** — он остаётся
   вычисляемым контекстом; хранится только срок, как модификатор явного `disarmed`. Снятие или
   постановка зоны по-прежнему меняет `Bypassed` без записи в устройство.
3. **Журнал и права.** Права — существующие `control` (запись) и `view`/`view_live` (чтение);
   отдельного `maintenance_bypass` здесь нет — он из пожарного плана 13 и панельного плана 12.
   Журнал — существующая строка `arming`: `param0` — оператор, `param1` — режим, а срок и факт
   истечения идут в `extra_props` (`until`, `expired`). При истечении актор — `system`. Это
   честный аудит: срок теперь хранимое намерение, а не вычисляемый контекст. Отдельного события
   `bypassed` нет — сам обход производный и может появиться/исчезнуть без действия оператора.
4. **Объём.** Входит: вычисление `bypassed` для камер, датчиков и контейнеров; необязательный
   `until` с авто-возвратом; действие на зоне целиком; proto/web_srv/UI; группировка тревог
   исключённого устройства под его зоной; миграции, sweep, тесты, живая проверка, документация,
   сокращение future plan 20. Не входит: повторяющееся расписание («каждый день с 22:00»),
   отдельное право и полный аудит, очистка явных решений датчиков при возврате зоны, пожарная
   панель и `arm_policy`, группа-примитив, панельная логика, не-камерные источники, таймер на
   режим `armed`, переименование существующей группы тревог без зоны.

## Решения

- **Обход — свойство любого armable-узла.** Устройство (`DevicePart`, `DigitalInputPart`) и
  контейнер (`space`, фигура-зона, мнемосхема), через который проходит опорная реплика. Для
  контейнера это и есть «снять комнату целиком»: резолвер уже разрешает всех потомков через зону,
  отдельной записи на датчики нет. Явные решения датчиков внутри зоны не переписываются.
- **Обход комнаты и её датчики — явная матрица.** Наследуемый датчик следует комнате: пока она
  обойдена, новых тревог не поднимает; после возврата снова наследует здание. Явно поставленный
  датчик остаётся на охране, явно снятый — снят; комната их не переписывает. Уже открытые тревоги
  обход не закрывает и не прячет: они остаются в `alarms` и возвращаются `GetAlarms`. Меняется
  только ключ группы: наследуемый датчик остаётся под комнатой (`source=space`, даже когда комната
  снята или обойдена); явно решённый — без зоны, как и раньше, потому что его решил собственный
  режим. Пустая группа означает «ни одна зона не решила», а не «датчик не на охране»; её
  существующую подпись в этой задаче не меняем.
- **Правило вывода** — в резолвере, не в клиенте:
  `Bypassed = ownMode == disarmed && nearestExplicitContainerAbove == armed`. Для устройств
  используется существующий обход вверх от опорной реплики (`SpaceArmedManyAsync`); для
  контейнеров — обход от родителя, только когда собственная строка `armed=false`. Явно
  поставленные узлы обход не считают.
- **Гарантия: вычисление `bypassed` не меняет результат `armed`.** Это добавка к чтению, а не
  новая логика охраны. Матрица, которую фиксируют тесты:

  | собственный режим | ближайшая явная зона выше | `armed` | `bypassed` |
  |---|---|---|---|
  | `inherit` | `armed` | true | false |
  | `inherit` | `disarmed` | false | false |
  | `disarmed` | `armed` | false | true |
  | `disarmed` | `disarmed` | false | false |
  | `armed` | `disarmed` | true | false |
  | `disarmed` | строки нет | false | false |

- **Смысл `SourceObjectId` расширяется явно.** Сейчас это «узел, чья строка определила ответ»;
  при обходе зона определила контекст, но не сам `Armed=false` (его определило собственное
  `disarmed`). Поле продолжает означать «зона, к которой относится ответ охраны»: победившая
  строка или охраняемая зона, из которой узел исключён. Это закрепляется в комментариях proto и
  docs и в именах тестов. Отдельное `BypassSourceObjectId` сейчас не вводим (YAGNI); если
  появятся другие контекстные состояния, поле разделим.
- **Срок — модификатор `disarmed`, не отдельный режим.** `until` необязателен (пусто — бессрочно).
  Валидация: только с `disarmed`; время в будущем; с `armed`/`inherit` — `InvalidArgument`.
  Любая явная смена режима снимает таймер.
- **Возврат по сроку — в прежний собственный режим.** `restore_mode` вычисляется при постановке
  таймера: был явный `armed` → `armed`; был `inherit` → `inherit`; был `disarmed` без таймера →
  `inherit`; повторный таймер на уже обойдённом узле сохраняет прежний `restore_mode`. Так
  «временно снять комнату» действительно возвращает её на охрану, а обход датчика в зоне — снова
  следует зоне. **Продление срока не меняет `restore_mode`:** комната была `armed`, снята до
  18:00, продлена до 20:00 — в 20:00 вернётся в `armed`, а не в `inherit`. Это закреплено тестом.
- **Время — абсолютный UTC.** Диалог принимает локальные дату и время (`datetime-local`), клиент
  переводит их в `Date` и отправляет `toISOString()`; сервер не парсит локальные строки, хранит
  `timestamptz` и сравнивает с `now()` в UTC. Переход на летнее/зимнее время не создаёт
  двусмысленности: хранится момент, а не настенное время. Несуществующее или неоднозначное
  локальное время разрешает браузер, а UI показывает получившийся момент для подтверждения.
- **Истечение — durable sweep в VmsMap.** `BackgroundService`, интервал 15 с, `FOR UPDATE SKIP
  LOCKED` (образец — `OutboxPublisher`), одна транзакция на пачку: вернуть режим, снять таймер,
  записать `arming` с актором `system` и `extra_props.expired`. Для контейнера — строка
  `map-arming-changed` в `map_outbox` в той же транзакции, чтобы подписчики пересчитали датчики;
  для устройства — существующий `ReDerive` после коммита. Падение откатывает транзакцию, таймер
  остаётся до следующего тика; реплики делят строки через `SKIP LOCKED`.
- **Замкнутый контакт в момент истечения поднимает тревогу сразу** — это страховка от забытого
  обхода. При постановке таймера UI предупреждает об этом.
- **Тревоги обойдённого устройства — в группе его зоны.** `ArmingZonesAsync` включает
  `Source == space` и `Bypassed` с непустым `SourceObjectId`: зона уже находит устройства по
  опорной реплике и принимает их тревоги массово (`AcknowledgeSubtreeAlarms`). Группа «Not on
  guard» перестаёт противоречить этому.
- **«Return to zone» — существующий `inherit`.** Для устройства — кнопка в `ObjectControl`; для
  контейнера — уже есть кнопка `Inherit` в `NodeGuard`. Отдельного RPC нет.
- **Цвет и подпись.** «Bypassed» — янтарный (`warning.main`): серый занят «не на охране»,
  фиолетовый — «на охране через предка». Со сроком подпись говорит время: «Bypassed until 18:00»;
  узел, снятый с таймером без охраняемого предка, — «Off guard until 18:00».

## Хранение и миграция

Новая миграция `dotnet/VmsMap/Grpc/migrations/0005_arming_bypass.sql`:

```sql
ALTER TABLE object_arming
  ADD COLUMN bypass_until timestamptz,
  ADD COLUMN restore_mode text,
  ADD CONSTRAINT object_arming_bypass_pair
    CHECK ((bypass_until IS NULL) = (restore_mode IS NULL)),
  ADD CONSTRAINT object_arming_bypass_restore
    CHECK (restore_mode IS NULL OR restore_mode IN ('inherit', 'armed')),
  ADD CONSTRAINT object_arming_bypass_disarmed
    CHECK (bypass_until IS NULL OR NOT armed);

ALTER TABLE device_object_settings
  ADD COLUMN bypass_until timestamptz,
  ADD COLUMN restore_mode text,
  ADD CONSTRAINT device_object_settings_bypass_pair
    CHECK ((bypass_until IS NULL) = (restore_mode IS NULL)),
  ADD CONSTRAINT device_object_settings_bypass_restore
    CHECK (restore_mode IS NULL OR restore_mode IN ('inherit', 'armed')),
  ADD CONSTRAINT device_object_settings_bypass_disarmed
    CHECK (bypass_until IS NULL OR arm_mode = 'disarmed');

CREATE INDEX idx_object_arming_bypass_due
  ON object_arming (bypass_until) WHERE bypass_until IS NOT NULL;
CREATE INDEX idx_device_settings_bypass_due
  ON device_object_settings (bypass_until) WHERE bypass_until IS NOT NULL;
```

Модели: `Db/Models/DBObjectArming.cs`, `Db/Models/DBDeviceObjectSettings.cs`, маппинг в
`Db/PgDbContext.cs`. Время хранится в UTC; в UI показывается в местном поясе.

## Сервер (VmsMap)

1. `Domain/Arming/EffectiveArmingResolver.cs`
   - `NodeArmingInfo` получает `bool Bypassed`, `DateTime? BypassUntil`.
   - Устройства: явно снятые субъекты добавляются к тем, для кого нужен ответ зоны; один
     `SpaceArmedManyAsync` на `inherit ∪ disarmed`; `Bypassed` и `SourceObjectId` — из ответа
     зоны; `BypassUntil` — из строки настроек.
   - Контейнеры: `ResolveSpaceAsync` при собственной строке `armed=false` продолжает обход вверх;
     ближайшая явная строка предка `armed=true` даёт `Bypassed`, её узел — `SourceObjectId`;
     `BypassUntil` — из собственной строки. `armed=true` и отсутствие строки — как сейчас.
2. `Domain/Arming/DeviceObjectSettingsStore.cs` — запись/чтение `bypass_until` и `restore_mode`.
3. **Один путь записи устройства (исправление из ревью).** Сейчас `SetNodeArming` идёт в
   `NodeArmingService.SetDeviceAsync` и не делает `ReDeriveAsync`, а `SetDeviceArming` идёт через
   `DeviceSettingsService.SetArmingAsync` и делает. Из-за этого «Return to zone» из
   `ObjectControl` (`setNodeArming`) вернул бы устройство в зону, но не обновил бы флаг камеры в
   VmsCfg и не пересчитал бы тревогу датчика; та же дыра есть у постановки камеры в `inherit`.
   Выбран **первый вариант ревью — починить общий путь**, а не переключать кнопку на второй
   фасад: иначе останутся два конкурирующих способа записи.
   - Новый маленький помощник `Domain/Arming/DeviceArmingDerivation.cs`: камера →
     `CameraArmingReconciler.ReconcileAsync(cameraId)`, цифровой вход →
     `SensorAlarmService.ReconcileCameraAsync(cameraId)`. Это единственное место пересчёта.
   - `NodeArmingService.SetDeviceAsync` после коммита всегда зовёт помощника — для `armed`,
     `disarmed` и `inherit`, для камеры и входа. Прямая запись флага камеры через
     `SetCamArmedAsync` убирается: флаг выводит тот же reconciler, что и для `inherit`, поэтому
     явный и унаследованный режимы идут одним путём.
   - `DeviceSettingsService.SetArmingAsync` перестаёт вызывать `ReDeriveAsync` отдельно (путь
     записи уже сделал это один раз); `SetAsync` админской карточки (режим + правило тревоги)
     зовёт того же помощника после изменения правила.
4. `Domain/Arming/NodeArmingService.cs` — срок и обход.
   - `SetAsync` принимает `DateTime? until`; вычисляет `restore_mode` по прежнему собственному
     режиму; пишет режим, таймер и `restore_mode` в одной транзакции; журнал `arming` с
     `extra_props.until`.
   - Для контейнера: `changed` по эффективному `armed` управляет outbox-строкой; смена только
     таймера пишет строку и журнал, но не outbox.
   - `ExpireBypassAsync(objectId)` — записывает сохранённый `restore_mode`, снимает таймер, журнал
     с актором `system` и `extra_props.expired`, outbox при смене эффективного значения. Для
     устройства идёт через тот же путь записи, поэтому пересчёт выполняется.
5. `Domain/Arming/DeviceSettingsService.cs` — прокидывает `until` и `restore_mode`, полагается на
   общий путь записи; `ExpireBypassAsync` для устройства по `DeviceSettingsKey` делегирует в тот
   же сервис.
6. `Adapter/ArmingBypassSweep.cs` — новый `BackgroundService`: claim пачки по обеим таблицам,
   вызов expiry-сервиса, `TimeProvider` для тестов, интервал 15 с.
7. `Domain/Control/ControlObjectQuery.cs` — `ControlObjectRow` получает `Bypassed` и `BypassUntil`.
8. `Grpc/MapServiceImpl.Alarms.cs` — `ArmingZonesAsync` включает обойдённые устройства.
9. Proto-маппинг в `Grpc/MapServiceImpl.Control.cs`, `Grpc/MapServiceImpl.Settings.cs`,
   `Grpc/MapServiceImpl.Reads.cs`.
10. `proto/vms_map.proto`:
   - `SetNodeArmingRequest` и `SetDeviceArmingRequest` — `optional google.protobuf.Timestamp until = 4;`
     (только с `disarmed`; пусто — бессрочно; прошедшее время — `InvalidArgument`);
   - `NodeArming` — `bool bypassed = 5; optional google.protobuf.Timestamp bypass_until = 6;`;
   - `ControlObject` — `bool bypassed = 20; optional google.protobuf.Timestamp bypass_until = 21;`;
   - обновить комментарии `source_object_id` и `arming_source_object_id` (зона обхода тоже);
   - поправить устаревший комментарий `SetDeviceArmingRequest.mode`: фасад уже принимает
     `inherit` (`ArmingPolicy.DeviceModes`), и карточка I/O его пишет.

## web_srv

- `dotnet/web_srv.Server/Controllers/ControlController.cs` — `NodeArmingDto`, `ControlObjectDto`,
  `ObjectSettingsDto` получают `Bypassed` и `BypassUntil`; `SetArming` принимает
  `{ mode, until? }` и передаёт `Timestamp`; прошедшее время и `until` с не-`disarmed` — 400 через
  `InvalidArgument`.
- `dotnet/web_srv.Server/Controllers/DeviceIoController.cs` — `TryArmingAsync` возвращает
  `bypassed` и `bypassUntil`; `SetDigitalInputArmMode` принимает `until?`.
- Эндпоинты и `[RequiresRight]` не меняются: запись — `control`, чтение — `view`/`view_live`.

## Фронтенд

- `dotnet/web_srv.client/src/dto/dtos.tsx` — `bypassed`, `bypassUntil` в `NodeArmingDto`,
  `ControlObjectDto`, строке цифрового входа и `ObjectSettingsDto`; `until?` в телах записи.
- Новый чистый помощник `src/components/control/guardState.ts` (+ `guardState.test.ts`, vitest
  берёт `src/**/*.test.ts`): принимает DTO и возвращает `{ labelKey, color, timed,
  canReturnToZone }` — не только подпись и цвет, но и доступные действия, чтобы `ObjectControl`,
  `DeviceIoPanel` и строка списка не интерпретировали комбинации `armed`/`source`/`bypassed`
  каждая по-своему. Форматирование времени — в компоненте через `Intl.DateTimeFormat`.
  Сейчас логика подписи продублирована в `ObjectControl` и `DeviceIoPanel` — сводим в одно место.
- Новый переиспользуемый диалог `src/components/control/BypassUntilDialog.tsx`: пресеты
  (30 мин, 1 ч, 2 ч, 4 ч) и произвольные дата/время; предупреждение «по истечении узел вернётся в
  охрану, замкнутый контакт поднимет тревогу»; кнопка «бессрочно».
- `src/components/control/ObjectControl.tsx` — подпись через помощник, янтарный цвет, кнопка
  «Bypass until…» (диалог) и «Return to zone» (`inherit`) в состоянии обхода.
- `src/components/control/NodeGuard.tsx` — у зоны: подпись «Bypassed» / «Off guard until …»,
  кнопка «Bypass until…» рядом с режимами; существующий `Inherit` снимает обход и таймер.
- `src/components/control/ControlPanel.tsx` — янтарная иконка щита у строки с `bypassed`.
- `src/components/DeviceIoPanel.tsx` — подпись через помощник; `Select` режимов уже предлагает
  `inherit`; таймер — через общий диалог рядом с `Select`.
- `src/components/objects/ObjectSettingsCard.tsx` — подсказка «Bypassed» / «until» рядом с
  эффективным состоянием; список режимов не меняется.
- i18n: `control.guardBypassed`, `control.guardBypassedUntil`, `control.guardOffUntil`,
  `control.bypassUntil`, `control.bypassUntilHint`, `control.bypassForever`,
  `control.returnToZone` в `locales/en/translation.json` и `locales/es/translation.json`.
  Русской локали во фронтенде нет.
- Список тревог менять не нужно: группировка приходит с сервера.

## Тесты

- `VmsMap.Tests/EffectiveArmingTests.cs` — полная матрица из раздела «Решения»: `bypassed`
  никогда не меняет `armed`; устройство и камера при явном `disarmed` под охраняемой зоной —
  обход (расширить `ADisarmedInputStaysDisarmedEvenWhenItsSpaceIsArmed`); снятие зоны делает
  `bypassed=false` без записи в устройство, повторная постановка — снова `true`; перенос опорной
  реплики в другую зону пересчитывает относительно нового места; без опорной реплики обход не
  определяется; контейнер: собственная `armed=false` под охраняемым предком — обход, под снятым —
  нет, `inherit` — нет; `bypassUntil` приходит из строки.
- `VmsMap.Tests/SpaceArmingTests.cs` — `until` пишет `bypass_until`/`restore_mode`; `restore_mode`
  из прежнего режима (`armed`/`inherit`/`disarmed`); повторный таймер сохраняет `restore_mode`;
  явная смена режима снимает таймер; `until` в прошлом и с `armed`/`inherit` — отказ; журнал
  содержит `extra_props.until`; смена только таймера не пишет outbox.
- **Оба пути записи устройства дают одинаковый побочный эффект** (главный тест из ревью):
  `SetNodeArming` и `SetDeviceArming` с `inherit` для камеры обновляют флаг в VmsCfg; для
  цифрового входа — запускают пересчёт и поднимают тревогу при замкнутом контакте; то же для
  `armed`/`disarmed`. Это же покрывает «Return to zone» и истечение срока.
- **Сценарии тревог (устройство):** обход не скрывает уже открытую тревогу; после возврата в
  `inherit` она снова учитывается обычным механизмом (`source=space`, зона в
  `armingSourceObjectId`); если зона снята, пока устройство обойдено, тревога остаётся, а группа —
  «Not on guard» (зоны нет).
- **Обход комнаты с тремя датчиками** — главный сценарий второго ревью: здание `armed`, комната
  `disarmed` (bypassed), датчик 1 `inherit`, датчик 2 `inherit`, датчик 3 явно `armed`.
  До обхода 1 и 2 поднимают тревоги через комнату; обход комнаты — новых тревог у 1 и 2 нет,
  их открытые тревоги остаются и группируются под комнатой, датчик 3 продолжает поднимать тревоги
  (его группа — без зоны, как и раньше); возврат комнаты — 1 и 2 снова на охране, замкнутый
  контакт поднимает тревогу через outbox, датчик 3 не меняется. Явно снятый датчик 4 внутри
  комнаты остаётся снятым и не попадает в её группу.
- **Продление таймера:** комната была `armed`, снята до 18:00, продлена до 20:00 — `restore_mode`
  остался `armed`, в 20:00 режим `armed`.
- **Время:** `bypass_until` хранится `timestamptz` и сравнивается в UTC; клиентская конвертация
  локального `datetime-local` в `toISOString()` покрыта тестом на фиксированном моменте, без
  привязки к поясу машины.
- `VmsMap.Tests/SensorAlarmTests.cs` — обойдённый датчик не поднимает тревогу; истечение при
  замкнутом контакте поднимает; открытая до обхода тревога переживает его.
- `VmsMap.Tests/ControlObjectTests.cs` — строка несёт `Bypassed`/`BypassUntil`; `GetAlarms`
  группирует тревогу обойдённого устройства под зоной.
- Новый `ArmingBypassSweepTests` — поддельные часы: будущий таймер не трогается; истёкший
  возвращает `inherit`/`armed` и снимает таймер; журнал `system`/`expired`; outbox у контейнера;
  пересчёт у устройства; операторская смена режима отменяет таймер и не попадает в claim.
- Postgres (при `VMS_MAP_TEST_DSN` на `ta_map_test`): `FOR UPDATE SKIP LOCKED` — две «реплики»
  обрабатывают строку один раз; откат транзакции оставляет таймер для следующего тика.
- `web_srv.Tests` — маппинг `bypassed`/`bypassUntil`, передача `until`, `[RequiresRight]` не
  изменились; прошедшее время — 400.
- Фронтенд — `guardState.test.ts`: приоритет «bypassed важнее source», «со сроком», цвета и
  ключи подписей; тест форматирования времени, если оно выносится в помощник.

## Живая проверка

1. `docker compose up -d postgres rabbitmq mediamtx db-init`
2. `scripts/dev-up.sh --build`
3. `tests/onvifsim/onvifsim_lab.py up --world maryland`
4. `python3 tests/demo/check_demo.py --provision-cameras`

Дополнить `tests/demo/check_demo.py` (рядом с `check_zone_arming`, всё в `try/finally`):
- поставить `plan1` на охрану; снять `sim-master/input` с `until = now + 10 s` → строка
  `armed=false`, `bypassed=true`, `armingSourceObjectId=plan1`, `bypassUntil` заполнен; замкнуть
  `loop` → **новой тревоги нет**;
- дождаться истечения (sweep 15 с, запас до 90 с) → `bypassed=false`, `source=space`,
  `armed=true`, тревога поднялась (контакт замкнут), в журнале — `arming` с `param0="system"` и
  `extra_props.expired`;
- зона целиком: поставить `map1` на охрану, снять `plan1` с `until` → наследуемый
  `sim-master/input` следует помещению и новых тревог не поднимает, явно поставленный
  `sim-ptz/input` продолжает работать; по истечении `plan1` снова следует `map1`, а замкнутый
  контакт `sim-master/input` поднимает тревогу;
- камера: в `check_camera_zone_arming` — временный обход с авто-возвратом.

Браузер (Playwright, `/operator/control`): выбрать пол, увидеть «Bypassed until …», поставить
короткий таймер, увидеть подпись и авто-возврат; проверить «Return to zone». После проверки
остановить Vite, стенд и симулятор, демо-мир восстановить. Незакоммиченные `.vscode/launch.json`
и `.claude/env-profiles` не трогать.

**Найдено на стенде (2026-11-21).** Прогон `check_demo.py` падал не на обходе, а на устройстве:
постановка зоны пишет флаг каждой камеры в VmsCfg, `server-config-changed` заставляет VmsOnvif
снять подписку на I/O **вместе с манифестом частей**, и до ближайшего чтения списка или прохода
раз в 5 минут события входа приходят без полярности (`polarity_known=false`) — карта их
отбрасывает. Переход контакта в этом окне терялся, тревога не снималась. Исправлено в VmsOnvif:
смена конфигурации снимает подписку, но оставляет манифест, а перезвон новой подписки теперь
объявляется карте (раньше он молча складывался в `_reported`). В `check_demo.py` команда реле,
от которой ждут перехода, повторяется через обратное переключение — на случай, если переход
всё же попал в окно переподписки.

## Документация

- `docs/maps/control.md` — обход датчика и зоны, срок и `restore_mode`, «снять комнату целиком»,
  явные решения датчиков не переписываются, отдельного права/аудита нет.
- `docs/maps/states-and-alarms.md` — тревога обойдённого устройства группируется под зоной;
  истечение срока пересчитывает датчики и может поднять тревогу сразу.
- `docs/future_plans/20-zone-arming.md` — убрать пункт `bypassed` и упоминание автоснятия;
  оставить группу-примитив, пожарную политику, панельную логику и не-камерные источники;
  повторяющееся расписание остаётся в плане 12.
- `docs/future_plans/README.md` — поправить строку 20 (убрать `bypassed`; заодно отметить, что
  пункт про журнал карточек уже закрыт в M2.4i).
- `docs/plans/zone-arming-bypass.md` — этот план с чек-листом; отметки ставить по ходу работы.

## Порядок и коммиты

1. `docs: plan the zone-arming bypass` — создать `docs/plans/zone-arming-bypass.md`.
2. `VmsMap: derive a bypassed device or zone` — резолвер, proto, маппинги, группировка тревог,
   тесты; без хранения.
3. `VmsMap: store an arming bypass until a deadline` — миграция, модели, фасады, журнал, тесты.
4. `VmsMap: return a bypassed node to its previous mode` — sweep, `TimeProvider`, Postgres-тесты.
5. `web_srv: pass the bypass deadline through` — DTO, валидация, тесты.
6. `Frontend: show a bypass and let it expire` — помощник, диалог, компоненты, i18n, тесты.
6a. `VmsOnvif: keep a camera's parts when its configuration changes` — переподписка не стирает
   манифест, перезвон подписки объявляется карте (найдено на стенде, блокировало проверку).
7. `Demo: check the bypass and its deadline end to end` — `check_demo.py`, живая проверка.
8. `Docs: close the bypassed item of the future plan` — docs и сокращение future plan 20.

Проверки после соответствующих коммитов:

```
dotnet build dotnet/ta_vms.sln
dotnet test dotnet/VmsMap.Tests
dotnet test dotnet/web_srv.Tests
cd dotnet/web_srv.client && npm run build && npm run lint && npm test
```

Postgres при необходимости:

```
VMS_MAP_TEST_DSN="Host=localhost;Port=5432;Database=ta_map_test;Username=postgres;Password=postgres" \
  dotnet test dotnet/VmsMap.Tests
```

Push и merge в `master` — только по просьбе пользователя.

## Состояние

- [x] 1. План в репозитории.
- [x] 2. VmsMap: вычисляемый `bypassed` для устройства и зоны, прото, группировка тревог
  (`0e03db23`; `VmsMap.Tests` 431).
- [x] 3. VmsMap: хранение срока и `restore_mode`, журнал, единый путь записи устройства
  (`ffbdcf8d`; `VmsMap.Tests` 444).
- [x] 4. VmsMap: sweep истечения, `TimeProvider`, Postgres-тест claim/rollback
  (`b377aa1a`; `VmsMap.Tests` 451).
- [x] 5. web_srv: DTO, `until`, валидация (`98a9f4f8`; `web_srv.Tests` 25).
- [x] 6. Фронтенд: помощник, диалог, подписи, i18n (`98eb6ab4`; vitest 117, build и lint чистые).
- [x] 7. `check_demo.py`: обход датчика со сроком, возврат `system`/`expired`, обход помещения
  целиком и временный обход камеры. На стенде шаги `Zone bypass` и `Camera zone arming` прошли;
  полный прогон до конца не дошёл — стенд потерял маршрут Dapr до VmsCfg
  (`dial tcp 10.8.1.1:19012`, интерфейс VPN `amn0`), это окружение, а не код. Зелёный прогон
  целиком повторить при следующем поднятии стенда.
- [x] 8. Документация и сокращение future plan 20.
