# OnvifLib: переподключение pull point без потока в лог

> Ветка `fix/onvif-pull-point-retry` от `master` (4665f4f2 — слияние
> `fix/log-volume-leftovers`; план писался от `5cc6cdbe`). Исходник —
> `docs/future_plans/27-onvif-pull-point-retry-log.md` (удаляется при закрытии).
> Стенд — OnvifSim (`tests/onvifsim/`, `Sim.set_network`) и dev-стек, поднятый
> `scripts/dev-up.sh`. Затронуты два репозитория: `OnvifLib` (пакет `1.3.2`) и `ta_vms`
> (четыре `PackageReference`).

## Сделано

- **2026-11-18: OnvifLib `1.3.2`.** `FailureBackoff` (расписание записи) и правка
  `EventService1`; шесть тестов расписания, весь набор `OnvifLib.Tests` — 36/36. Ветка
  `fix/onvif-pull-point-retry` влита в `main`, тег `v1.3.2` запускает публикацию пакета.
- **2026-11-18: бамп в `ta_vms`.** Четыре `PackageReference` → `1.3.2`.
- **2026-11-18: базовый замер на стенде.** Камера `cam1` стенда (sim `master`, порт 8101)
  снята с сети через `Sim.set_network` после того, как стек подписался: 10 минут дали
  **931 строку** — 310 × `Re-subscribing`, 310 × `refused a new pull point`,
  310 × `Re-subscribe failed` и 1 × `Pull/Renew failed`, то есть ~310 попыток за 600 с
  (~1,94 с на попытку).

## Что видно

`OnvifLib.EventService1` подписывается на события один раз при создании
(`InitializeAsync` → `SetupPullPointAsync`), дальше `ReceiveLoopAsync` крутит long-poll
`PullMessages` (по умолчанию `PullTimeout` = 5 с) и продлевает подписку `Renew`
(`_terminationTime` = 30 с). Когда камера пропадает, работают два пути ошибки:

1. Пул или продление падает — `Pull/Renew failed ... will re-subscribe` (Error,
   `ServicesImp/EventService1.cs:749`), пауза 2 с (`:754`).
2. Следующая итерация с `needsResubscribe`: `ResubscribeAsync` пишет
   `Re-subscribing pull point` (Info, `:179`), `CreateSubscriptionAsync` падает —
   `LogRefusedSubscriptionAsync` успевает сходить в `GetServiceCapabilities` (дедлайн 2 с,
   `:28`) и пишет `The camera ... refused a new pull point` (Warning, `:274`), исключение
   ловится в цикле — `Re-subscribe failed ... will retry` (Error, `:652`), пауза 2 с (`:656`).

В устойчивом состоянии выключенной камеры это три строки на ~2,2 с и, сверх того, лишний
round trip в мёртвый endpoint на каждую попытку. Замер 2026-11-18 на стенде: 88 строк за
66 с на одну камеру — 30 Info, 29 Warning, 29 Error, то есть ~80 строк в минуту, около
115 тыс. строк (десятки мегабайт) в сутки на одну выключенную камеру.

Почему правится OnvifLib, а не VmsOnvif: `RunCameraSubscriptionAsync` после
`StartReceivingAsync` уходит в `Task.Delay(Infinite)`; его собственный ретрай (15 с / 30 с)
срабатывает, только если не удалось создать `EventService1` или упал `StartReceiving`.
Все повторные подписки уже внутри библиотеки. Тот же `EventService1` используют
`VmsOnvif.OnvifEventSubscriptionService` и `DeviceIoStateWatcher` (общий топик-фильтр
устройств), поэтому правка в `EventService1` закрывает оба.

## Решения

- **Попытки остаются каждые 2 с.** Подтверждено пользователем 2026-11-18. Это тот же
  выбор, что в `rtsp_reader`: неудачный connect к выключенной камере дёшев и ограничен
  одной попыткой в 2 с, а вернувшаяся камера подписывается снова в пределах ~2 с — для
  тревог по движению это важнее, чем экономия SYN'ов. Замедляется только запись.
- **Запись замедляется экспоненциально**: первая неудача пишется сразу, дальше интервал
  удваивается 2 → 4 → 8 → … → 300 с (потолок 5 минут, как
  `kMaxConnectFailureLogInterval` в `video_srv/reader/rtsp_reader.cpp`). Номер попытки в
  строке — накопительный счётчик неудач с момента последнего успешного `PullMessages`.
- **Одна строка на попытку, которая вообще пишется.** Оба пути ошибки (пул/продление и
  переподписка) идут через один счётчик: в цикле одна строка Error с номером попытки и
  причиной, вида `Re-subscribe failed for <url> (attempt N), will retry: ...`. Info
  `Re-subscribing pull point` из ретраев убирается.
- **`GetServiceCapabilities` — только когда строка пишется.** Диагностика `MaxPullPoints`
  (появилась в 1.3.1) переезжает в отчёт цикла: в подавленных попытках мёртвую камеру не
  опрашивают вовсе. Если камера ответила (её event-клиент открыт), к строке добавляется
  `; it allows N pull points at a time`. На начальной подписке и на смене фильтра
  предупреждение остаётся как сейчас — там оно одно на действие, а не поток.
- **Восстановление — одна строка Info.** После первого успешного `PullMessages` при
  накопленных неудачах: `Pull point for <url> re-established after N failed attempts`,
  счётчик и интервал сбрасываются. Сброс именно на успешном pull, а не на успешной
  подписке: камера, которая принимает подписку и не отдаёт события, не должна обнулять
  расписание на каждой итерации.
- **Смена фильтра — действие оператора.** Вместо общего `Re-subscribing pull point`
  `SetTopicFilterAsync` пишет одну Info `Applying topic filter '<expr>' for <url>`. Оба
  пути применения (в цикле и на остановленном цикле) проходят через этот метод.
- **Версия `1.3.2`**, без изменения публичного API. Бамп — только в `ta_vms`: четыре
  `PackageReference` в `VmsCfg`, `VmsOnvif`, `VmsOnvifActors`, `web_srv.Server`.
  `video_a` (C++) и `multitenant_admin` (только `KeycloakAdmin`) OnvifLib не подключают —
  бампать там нечего. `Square/AASubService` остаётся на `1.2.1`: репозиторий архивирован
  2026-11-18, и предыдущий релиз его сознательно не трогал.
- **Интервалы — константы в `EventService1`.** Публичных настроек не добавляем (YAGNI):
  потребности менять их из конфигурации нет.

## Шаги

### 1. `FailureBackoff` — расписание записи

Новый `internal sealed class FailureBackoff` (файл `ServicesImp/FailureBackoff.cs`,
namespace `OnvifLib`; тесты видят его через существующий `InternalsVisibleTo`).
Чистая логика без таймеров — «сейчас» передаётся параметром, поэтому тестируется без
камеры:

- `int Attempts` — неудач подряд;
- `bool Due(DateTime now)` — пора ли писать (по умолчанию, до первой записи, — да);
- `void Record(DateTime now)` — `Attempts++`; если `Due`, сдвинуть `_nextReportAt` на
  текущий интервал и удвоить интервал до потолка;
- `bool Reset()` — обнулить счётчик и интервал; вернуть, была ли серия (для строки
  восстановления).

В `EventService1` — поле `_failureBackoff = new(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5))`.

### 2. `EventService1`: цикл и точки записи

- В `ReceiveLoopAsync` оба catch-блока (`Pull/Renew failed` и `Re-subscribe failed`)
  считают неудачу через `_failureBackoff`. Строка пишется, только если `Due`; в ней —
  `attempt {Attempts}` и причина.
- В catch переподписки (при `Due`) добавляется диагностика `MaxPullPoints`: существующий
  `ReadMaxPullPointsAsync` вызывается уже здесь; он сам возвращает `null`, если
  event-клиент не открыт, поэтому мёртвая камера лишнего round trip не получает.
- `ResubscribeAsync` теряет строку Info. У `CreateSubscriptionAsync` появляется параметр
  «писать ли отказ» — цикл передаёт `false`, начальная подписка и смена фильтра `true`
  (там `LogRefusedSubscriptionAsync` остаётся как есть).
- После успешного `PullMessagesAsync`: `if (_failureBackoff.Reset()) logger.Info(
  "Pull point for <url> re-established after N failed attempts")`.
- `SetTopicFilterAsync` пишет Info `Applying topic filter ...` перед применением.
- Строки смены auth-схемы (`TrySwitchToChallenged`) не меняются: это разовое переключение,
  не устойчивый отказ.

### 3. Тесты OnvifLib

`tests/OnvifLib.Tests/FailureBackoffTests.cs`, контрактные имена и именованные константы:

- первая неудача пишется сразу;
- следующая — не раньше интервала, затем интервал удваивается;
- интервал упирается в потолок и дальше не растёт;
- подавленные попытки всё равно увеличивают `Attempts`;
- `Reset` возвращает `true` один раз, после него расписание начинается заново.

`dotnet test tests/OnvifLib.Tests` должен остаться зелёным целиком (сейчас зелёные
только парсерные тесты — цикл камерой не покрыт).

### 4. Релиз OnvifLib и бамп в ta_vms

1. `OnvifLib`: `<Version>` → `1.3.2`, `PackageReleaseNotes` — абзац о расписании записи;
   коммиты `Back off the pull-point retry log` и `1.3.2`; push в `main`; тег `v1.3.2`
   публикует пакет (CI сверяет тег с `<Version>`).
2. Дождаться индексации nuget.org; при необходимости `dotnet restore --no-http-cache`.
3. `ta_vms`: четыре `PackageReference` → `1.3.2`, коммит `Use OnvifLib 1.3.2` (отдельно от
   правок плана); `dotnet build dotnet/ta_vms.sln` и `dotnet test dotnet/ta_vms.sln`.

Push и тег — часть заказанного релиза; merge в `main`/`master` — по общему правилу, только
по просьбе.

### 5. Стенд OnvifSim: до и после

Камера стенда — `sim-master` (есть реле, значит проверяется и `DeviceIoStateWatcher` с
топик-фильтром). Подписка включается обычным путём:
`POST /api/onvif/cameras/{cameraId}/subscription` через web_srv (порт по умолчанию 5134),
в логе VmsOnvif появляется `Starting ONVIF event subscription`. Сеть камеры выключается и
включается через `Sim.set_network(cameraId, online)` (`tests/onvifsim/simapi.py`), остальные
камеры стенда продолжают работать.

**До правки** (текущий `master`): 10 минут с выключенной сетью камеры; зафиксировать число
строк трёх сообщений в логе VmsOnvif и число неудачных попыток. Ожидание по прошлому
замеру — около 800 строк за 10 минут (~80/мин).

**После правки**: те же 10 минут. Ожидаемое расписание (попытки каждые ~2,2 с):
записи примерно на 0, 2, 6, 14, 30, 62, 126, 254, 510 с — около 9 строк за 10 минут, затем
одна строка в 5 минут. После `online = true`:

- подписка восстанавливается на ближайшей попытке (в пределах ~2 с);
- в логе ровно одна Info о восстановлении с числом неудач;
- новое событие (реле `master` или человек в кадре) доходит до
  `GET /api/onvif/cameras/{id}/recent-events` в пределах нескольких секунд;
- OnvifSim не создаёт pull point'ов за время простоя и создаёт ровно один после
  восстановления; до `MaxPullPoints` (10) далеко;
- смена топик-фильтра (`DeviceIoStateWatcher` на реле `master`) по-прежнему применяется:
  состояния реле в панели обновляются.

Если стенд покажет, что при камере, выключенной **до** старта, поток даёт собственный
ретрай VmsOnvif (`ONVIF subscription error ... retrying in 15s`), это отдельная тема:
записать новый файл в `docs/future_plans/`, в этот план не тянуть.

### 6. Закрытие

- Отметить сделанное в этом файле по ходу работы.
- Удалить `docs/future_plans/27-onvif-pull-point-retry-log.md` и его строку из
  `docs/future_plans/README.md`.
- Перенести этот файл в `docs/plans/archive/` с пометкой «Закрыт <дата>» и итогом замера
  (как `docs/plans/archive/log-volume-leftovers.md`).

## Коммиты

- `OnvifLib`: `Back off the pull-point retry log`, `1.3.2` (тег `v1.3.2`);
- `ta_vms`: план (`Plan the OnvifLib pull-point retry log`), `Use OnvifLib 1.3.2`,
  `Close the OnvifLib pull-point retry log` (удаление future plan).

Push, тег и PR — по просьбе; план на ветке `fix/onvif-pull-point-retry`.

## Что сознательно не делаем

- Не замедляем сами попытки — решение выше.
- Не трогаем собственный ретрай VmsOnvif (15 с / 30 с) и его сообщения: он срабатывает
  только при неудачном создании сервиса, а не в устойчивом цикле подписки.
- Не бампаем OnvifLib в `video_a`, `multitenant_admin` (нет ссылки) и `Square` (архив).
- Не добавляем публичные настройки интервалов и не меняем публичный API библиотеки.
- Не меняем семантику переключения auth-схемы и логику `Renew` — только момент записи.
