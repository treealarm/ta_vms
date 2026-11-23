# Присутствие камеры в сети и видео: два независимых состояния

> Ветка `feature/camera-connection-state` от `master`.
>
> Исходник — `docs/future_plans/07-camera-connection-state.md`. Файл future plan по завершении
> переезжает в `legacy/` с записью о сделанном, строка в его README удаляется.

**Результат.** `CONNECTED`/`DISCONNECTED` отвечают на вопрос «камера проявляется в сети
хоть как-нибудь»: отдаёт поток, отвечает на ONVIF-запрос или шлёт ONVIF-событие. Отсутствие видео —
отдельное **нетревожное** состояние `NO_VIDEO`, которое комбинируется с любым состоянием связи.
Карта узнаёт о смене видео событием `camera-video-changed` от VmsFs и перечитывает состояние у
него же; второй канал — лёгкий пробный запрос в VmsOnvif, который карта делает только для камер с
известной проблемой видео (мёртвый поток или отсутствие потоков). Опрос раз в 5 секунд и
зависимость `DISCONNECTED` от `recording_expected` уходят.

## Что не так сейчас

- `ConnectionStatePoller` (VmsMap) раз в 5 с перечисляет **все** камеры и спрашивает у VmsFs
  открытые обрывы источника (`ListSourceOutages`); открытый обрыв = `DISCONNECTED`. При 100 000
  камер это полный список каждые 5 с, а `ListSourceOutages` ограничен 1000 строками и сортирован
  по `started_ts DESC` — часть открытых обрывов не поместится, и камера покажется подключённой.
- Обрыв пишется только потоку с `recording_expected == true` (proto это прямо оговаривает: без
  записи авария не стоит футажа). Поэтому камера без записи на стенде осталась `CONNECTED` — не
  потому, что поток жив, а потому, что обрыв ей не положен.
- «Камера в сети» и «камера даёт видео» — разные вещи. Камера с неработающим потоком может
  отвечать по ONVIF; камера вовсе без потоков — легальная конфигурация (VmsCfg её принимает,
  video_srv не создаёт для неё `cam_device`). Сегодня такие камеры получают `CONNECTED` по
  умолчанию — догадка, а не наблюдение.
- `DISCONNECTED` — **тревожное** состояние (`object_state_descriptions.alarm = true`): зажигает
  пространства и счётчики. Ставить его по отсутствию видео — путать тревогу о связи с проблемой
  картинки.

## Модель

### 1. Два независимых признака, а не один «камера работает»

- **Связь** (`CONNECTED`/`DISCONNECTED`) — присутствие камеры в сети. `DISCONNECTED` ставится,
  только когда камера не ответила **ни по одному известному каналу**. Тревожное.
- **Видео** (`NO_VIDEO` или отсутствие состояния) — ни один поток камеры не отдаёт кадров.
  Нетревожное, янтарное, комбинируется с любым состоянием связи.

`UNKNOWN` — полноценное наблюдение: «не знаем» никогда не превращается в «нет» и не меняет
состояние. `CONNECTED` по умолчанию (потому что обрыва нет) больше не существует.

### 2. Каналы

1. **Видео** — `source_alive` по каждому потоку, который video_srv снимает читателем постоянно
   (читатель есть у любого невыключенного потока, независимо от записи и зрителей).
2. **ONVIF-ответ** — камера отвечает на запрос по своему ONVIF-адресу. Критерий — **сетевой**:
   HTTP-ответ, SOAP Fault или 401 из-за неверных учётных данных тоже доказывают присутствие.
   Ошибка авторизации или неподдерживаемая операция — это «камера есть», а не «камеры нет».
3. **ONVIF-событие** — любое событие от камеры доказывает присутствие. Сигнал только
   положительный: отсутствие событий ничего не опровергает.

Камеры без ONVIF канала не имеют — это отсутствие канала, а не отказ. Камера без потоков имеет
только ONVIF. Камера вовсе без каналов (нет ни потоков, ни ONVIF) не наблюдаема: её состояние
не трогаем (иначе она навсегда красная).

### 3. Агрегация потоков камеры

У потока три состояния: `Alive`, `NoVideo`, `Unknown`. Для камеры:

| Потоки камеры (невыключенные) | Видео камеры |
|---|---|
| хотя бы один `Alive` | видео есть |
| все известны и все `NoVideo` | `NO_VIDEO` |
| нет `Alive`, но есть `Unknown` | видео неизвестно (без `NO_VIDEO`) |
| нет ни одного известного | видео неизвестно |

`NO_VIDEO` — это **доказанное** отсутствие видео на всех потоках, а не «один поток умер». Поток,
о котором ещё не слышали (только добавлен, не успел отчитаться после старта), не превращается в
неисправность и не маскируется живым соседом.

### 4. Правило сведения каналов

Probe спрашиваем, только когда у камеры **нет ни одного живого потока** и при этом есть
**доказательство проблемы**: хотя бы один поток известен `NoVideo` или потоков не настроено.
Одни только `Unknown` (нет отчётов, узел молчит, video_srv упал) — это «не наблюдали», probe не
зовём и состояние не трогаем: иначе падение video_srv обернулось бы опросом всех 100 000 камер
по ONVIF.

| Видео камеры | ONVIF | Результат |
|---|---|---|
| есть живой поток | не спрашиваем | `CONNECTED`, `NO_VIDEO` снят |
| `NO_VIDEO` (все потоки) | отвечает | `CONNECTED` + `NO_VIDEO` |
| `NO_VIDEO` (все потоки) | не отвечает | `DISCONNECTED` + `NO_VIDEO` |
| `NO_VIDEO` (все потоки) | нет ONVIF | `DISCONNECTED` + `NO_VIDEO` (видео — единственный канал) |
| `NO_VIDEO` (все потоки) | спросить не удалось | связь не меняем, `NO_VIDEO` ставим |
| есть `NoVideo`, но есть и `Unknown`, живых нет | отвечает | `CONNECTED`, `NO_VIDEO` не ставим |
| то же | не отвечает | `DISCONNECTED`, `NO_VIDEO` не ставим |
| то же | нет ONVIF / спросить не удалось | ничего не меняем |
| все потоки `Unknown`, отчётов нет | не спрашиваем | ничего не меняем |
| потоков не настроено | отвечает | `CONNECTED` (без `NO_VIDEO`) |
| потоков не настроено | не отвечает | `DISCONNECTED` (без `NO_VIDEO`) |
| потоков не настроено | нет ONVIF / спросить не удалось | ничего не меняем |

- «Спросить не удалось» (VmsOnvif недоступен, реестр не прочитался) — не ответ камеры: падение
  VmsOnvif не зажигает все камеры красным.
- Выключенная камера: `NO_VIDEO` снимаем (video_srv её потоки не читает), связь не трогаем.
- Ошибка чтения конфигурации камеры — это `Unknown` и `Retry`, а не удаление и не `DISCONNECTED`.

### 5. Видео-состояние — отдельный `SourceVideoTracker` в VmsFs

Видео-состояние не подмешивается в `SourceLivenessTracker`: тот пишет обрывы в БД, и его сбой не
должен терять присутствие. Новый класс — только память, без БД и Dapr, с одним замком; чистая
функция переходов, тестируется без инфраструктуры.

Правила для потока (вход — отчёт `alive`, `instance_id`, время):

- `alive=true` → `Alive` (в том числе на смене `instance_id` — камера уже отдаёт кадры);
- `alive=false` при предыдущем `Alive` → `NoVideo` сразу (video_srv уже отждал свой 5-секундный
  watchdog; ждать ещё `Grace` — значит показывать 20+ секунд молчания);
- `alive=false` при `Unknown`/новом потоке/новом прогоне → `Unknown`, пока поток не отчитывается
  мёртвым `Grace` (15 с) — старт video_srv не вспыхивает `NO_VIDEO`;
- пустой `instance_id` прогон не сбрасывает;
- тишина дольше `Stale` (60 с): молчит **весь узел** — это медиасервер, а не камеры → `Unknown`;
  молчит часть потоков → `NoVideo`; никогда не слышанный поток остаётся отсутствующим;
- чтение состояния само применяет `Stale`: устаревший `Alive` отдаётся как `Unknown` (кадров никто
  не подтверждал целое окно), а `NoVideo` от тишины сохраняется — это и есть вердикт об
  отсутствии; замолчал ли весь узел, решает sweep и переводит запись в `Unknown`;
- `PruneTo`/`Forget` — молча забыть.

VmsFs — единственный владелец этого состояния: только он знает, что тишина узла не говорит о
камере. Поэтому **публикует переходы он**, а не video_srv: событие всегда соответствует уже
сохранённому состоянию, и гонки «событие раньше отчёта» нет по построению. Наблюдение видео
записывается в трекер **до** вызова пишущего в БД `SourceLivenessTracker.ApplyAsync`, чтобы сбой
обрыва не потерял присутствие.

### 6. video_srv отчитывается на переходе, но не публикует

В цикле liveness-потока уже раз в секунду снимается `list_stream_states()`. Добавляется карта
предыдущих `(camera, stream) → alive`; при любом изменении (включая появление/исчезновение
потока) вызывается `send_liveness()` немедленно, до 15-секундного такта. Так VmsFs пересчитывает
видеосостояние через ~1 с после вердикта watchdog, а не через 15 с. Публикатора и топика в C++ нет.

### 7. RPC `ListSourceStates` — пакетно, только известные потоки

```proto
rpc ListSourceStates(ListSourceStatesRequest) returns (ListSourceStatesResponse);
enum SourceVideoState {
  SOURCE_VIDEO_STATE_UNKNOWN = 0;
  SOURCE_VIDEO_STATE_ALIVE = 1;
  SOURCE_VIDEO_STATE_NO_VIDEO = 2;
}
message ListSourceStatesRequest { repeated string camera_ids = 1; }
message SourceStreamState {
  string camera_id = 1; string stream_name = 2; SourceVideoState state = 3;
}
message ListSourceStatesResponse { repeated SourceStreamState streams = 1; }
```

- Возвращаются только потоки с известным состоянием; отсутствующий поток — «не слышали».
- Карта всегда передаёт явный список камер **пачками по 1000** (ограничение и по размеру
  gRPC-сообщения, и по цене сериализации; пакет больше не значит «весь узел одним ответом»).
- Это не `ListSourceOutages`: обрывы остаются архивной темой и от `recording_expected` зависят
  по-прежнему.

### 8. Событие `camera-video-changed`

Топик назван по видео, а не по связи: связь карта выводит из видео и ONVIF вместе. VmsFs
публикует переход в `Alive`/`NoVideo` (из `Unknown` тоже — карте нужно начальное состояние;
переходы **в** `Unknown` не публикуются) — **одно событие на камеру за пачку отчётов**, а не на
поток:

```json
{"camera_id": "..."}
```

`server_id` в payload нет: `ReportSourceLivenessRequest.server_id` — это `APP_ID` видео-сервера,
а не `server_id` VmsCfg; карта и так знает узел камеры через `ICameraCatalog`. Данные о состоянии
в событие не кладутся — подписчик только перечитывает. Публикация — fire-and-forget с записью
сбоя в лог (как `retention-event-created`).

### 9. ONVIF-probe: сеть, а не успех операции

`OnvifService.ProbeReachability(CameraRequest)` → `Outcome ∈ {REACHABLE, UNREACHABLE, NO_ONVIF,
UNKNOWN}`:

- `CameraConnCache.GetAsync` вернул `null` (VmsCfg не ответил / камеры нет) → `UNKNOWN`;
- `CameraConnData.Conn == null` (ONVIF не настроен) → `NO_ONVIF`;
- иначе:
  1. дешёвый ONVIF-вызов с таймаутом 2–3 с (кандидат — `Camera.GetDeviceTimeAsync`,
     GetSystemDateAndTime; если библиотека требует учётных данных — `GetServicesAsync`, которым
     уже проверяет доступность чтение входов); ответил → `REACHABLE`;
  2. вызов упал (401, SOAP Fault, таймаут) → **TCP-соединение** на `conn.Ip:conn.Port` с
     таймаутом ~1 с: соединилось → `REACHABLE` (камера в сети, просто не ответила операцией);
     не соединилось → `UNREACHABLE`.
- `Camera.GetDeviceTimeAsync()` не принимает токен — оборачивать `WaitAsync(связанный токен)`;
  таймаут ограничивает вызывающего, не сокет.
- Кеш результата ~10 с и single-flight на камеру; при неудаче `Camera` **не выселять** каждый раз
  (иначе 30-секундный опрос выбьет 5-минутный кеш `OnvifSettingsService` у каждой офлайн-камеры).
  Логировать на `Debug`, не `Warning`: офлайн-камеры иначе зальют лог.
- Карта опрашивает probe, только если у камеры **нет ни одного живого потока** и есть
  доказательство проблемы (хотя бы один поток `NoVideo` или потоки не настроены); одни `Unknown`
  probe не вызывают. Камеры с живым видео не получают ни одного лишнего ONVIF-запроса.
- `device_node.reachable` не трогаем: его ведёт reconciler снимков, на связь он не влияет.

С планом [`onvif-device-list-cache`](onvif-device-list-cache.md) пересечения нет: тот
кэширует конфигурацию реле и входов через присутствие; probe доступности — отдельный вызов,
присутствие не читает и не меняет. Общие только `CameraConnCache` и `GetOrCreate`.

### 10. Сведение на карте: правила, гонки, сериализация

Карта — слой, который видит оба канала и хранит состояния узлов.

- **Чистое правило** `CameraPresenceRules.Decide(...)` принимает сводку по камере (есть ли
  живые/мёртвые/неизвестные потоки, есть ли ONVIF) и исход probe; возвращает желаемый набор
  `CONNECTED`/`DISCONNECTED`/`NO_VIDEO`. Тестируется без БД и gRPC.
- **Запись** — один `CameraStateWriter.ChangeAsync` на изменившуюся камеру: весь набор
  `CONNECTED`/`DISCONNECTED`/`NO_VIDEO` пишется атомарно под глобальной блокировкой
  `ta_map_camera_state`, чужие состояния (`ALARM`, контакты) сохраняются.
- **Устаревшее решение не перезаписывает новое.** Глобальная блокировка защищает транзакцию, но
  не порядок решений: sweep мог прочитать «видео нет» до восстановления, а событие уже записало
  `CONNECTED`. Защита: sweep запоминает `observedAt = UtcNow` **до** чтения состояний из VmsFs и
  после пакетного чтения текущих состояний пропускает камеру, у которой `object_states.timestamp`
  новее `observedAt` (значит, туда уже записал кто-то с более свежим наблюдением). Внутри
  реплики обработка одной камеры сериализуется (очередь с дедупликацией), поэтому два события
  одной камеры не считаются одновременно.
- **Ошибка чтения конфигурации** — `Unknown` и `Retry`, не удаление. `catalog.GetAsync` бросает
  при сбое VmsCfg и возвращает `null` только на «камеры нет» — `null` можно `Drop`, исключение —
  нельзя.

### 11. Sweep: обязательная страховка

- **Старт и каждые 5 минут** — полный проход: серверы из `IMediaServerRegistry.ServerIdsAsync` →
  `ListCameras(serverId)` пачками → `ListSourceStates` пачками по 1000 → probe только камер с
  известным мёртвым потоком или без потоков (ограниченная параллельность, по умолчанию 8) →
  пакетное чтение текущих состояний → запись только различий, пачками со своим DI-scope (один
  `VmsMapDbContext` на сотню тысяч записей не держим).
- **Каждые 30 с** — только камеры с `NO_VIDEO` (запрос по частичному индексу, join с
  `device_node`) и камеры без потоков (набор — из последнего полного прохода): перепроверить
  ONVIF и снять/поставить связь. Видео для них перечитывается тем же вызовом.
- Полный проход — **обязательный** механизм самовосстановления: потерянное событие, падение
  подписчика между чтением и записью, камера без потоков, удаление потока, старт реплики. Без
  него eventual consistency не гарантирована брокером.
- Всплеск (`Unknown → Alive` после перезапуска video_srv/VmsFs) не должен превращаться в
  неконтролируемую очередь: подписчик кладёт `camera_id` в набор с дедупликацией, дренаж раз в
  секунду с ограниченной параллельностью; повторное событие той же камеры до обработки
  схлопывается, промежуточные состояния не применяются — к моменту обработки читается актуальное.
  Сообщение подтверждается после постановки в набор; сбой обработки лечится полным проходом.

### 12. `recording_expected` — проверяется, а не правится вслепую

Флаг считается из политики записи (`override > base profile`, расписание важнее; невыключенный
`recordable`). Вероятная причина наблюдения в future plan: на стенде без `edge`-сценария у камер
нет профиля записи — флаг честен. В этом плане флаг **не меняется**: связь перестаёт от него
зависеть, архивные обрывы остаются как есть. Живая проверка подтверждает обе стороны. Если
клипы пишутся, а флаг `false`, это отдельный баг производителя
(`proto_mapper::recording_policy_for_stream` / `config_subscriber`) и он пишется в
`docs/future_plans/`.

### 13. Реплики, латентность, стоимость

- Состояния живут в `ta_map`, не в памяти реплики; событие доставляется одной реплике, запись
  идемпотентна, sweep сходится без событий.
- Ожидаемая реакция:
  - обрыв сети камеры при живом video_srv → watchdog 5 с + мгновенный отчёт (~1 с) + probe
    (≤3 с) → `NO_VIDEO`+`DISCONNECTED` примерно за 10–15 с;
  - пропало только видео (ONVIF отвечает) → `NO_VIDEO` примерно за 10 с, связь остаётся;
  - восстановление кадра → `Alive` за ~1 с, связь `CONNECTED`;
  - упал video_srv → камеры **не** краснеют: видео неизвестно (тишина узла), состояния
    сохраняются; это отказ медиасервера, а не камер;
  - потерянное событие → исправляется полным проходом ≤5 мин.
- 100 000 камер: полный проход — раз в 5 минут, перепроверка — только камеры без видео.
  Массовая смена (например, восстановление всего узла) пока пишет по камере после
  предварительного diff; пакетная запись одним заходом — только если нагрузочный стенд покажет
  узкое место (см. «Не входит»).

## Изменения

### proto

- **`proto/vms_fs.proto`** — `ListSourceStates` (§7).
- **`proto/onvif.proto`** — `OnvifService.ProbeReachability` + `ReachabilityOutcome` (§9).

### VmsFs

- **`SourceVideoTracker.cs`** (новый) — чистая машина состояний §5: `Observe`, `Decay`,
  `Snapshot(cameraIds)`, `PruneTo`, `Forget`, один замок; `Grace`/`Stale` — из
  `SourceLivenessTracker`.
- **`SourceVideoNotifier.cs`** (новый) — `ISourceVideoNotifier.Notify(cameraId)` +
  `DaprSourceVideoNotifier` (fire-and-forget, snake_case); одно событие на камеру.
- **`Grpc/SourceLivenessHandler.cs`** — сначала `videoTracker.Observe(...)` по всем потокам и
  `Notify` по изменившимся камерам, затем существующий `livenessTracker.ApplyAsync`; новый
  `ListSourceStates` из `Snapshot`.
- **`LivenessSweeper.cs`** — после `silent`/`wholeNodeSilent` вызвать `videoTracker.Decay(...)` и
  `Notify`; после `PruneTo` трекера обрывов — `PruneTo` видео-трекера.
- **`Program.cs`** — регистрация `SourceVideoTracker` и `ISourceVideoNotifier`.
- **`VmsFs.Tests/SourceVideoTrackerTests.cs`** — см. «Тесты».

### video_srv

- **`main.cpp`** — карта предыдущих `source_alive` в liveness-потоке и немедленный `send_liveness()`
  при изменении (§6). Без новых топиков и публикатора.

### VmsOnvif

- **`ReachabilityProbe.cs`** (новый) — `CameraReachabilityProbe` с `ICameraConnectionSource` и
  `IReachabilityReader.AnswersAsync(conn, ct)`; четыре исхода, TCP-фолбэк, кеш 10 с и
  single-flight.
- **`OnvifSettingsService.cs`** — `AnswersAsync`: `GetOrCreate(conn)` + ONVIF-вызов через
  `WaitAsync` связанного токена; отдельный TCP-connect с 1 с таймаутом.
- **`Grpc/OnvifServiceImpl.cs`** — RPC `ProbeReachability`.
- **`Program.cs`** — регистрация probe и reader (ручная сборка `OnvifServiceImpl`).

### VmsMap

- **`Clients/ICameraCatalog.cs` / `VmsCfgCameraCatalog.cs`** — `CameraInfo` получает
  `Disabled`, `VideoStreams` (имена невыключенных потоков) и `HasOnvif`; `ToInfo` заполняет из
  `CameraSetup`. Значения по умолчанию — чтобы существующие конструкции компилировались.
- **`Clients/IMediaServerRegistry.cs`** — `ServerIdsAsync()` из того же кэша
  `ListMediaServers`, чтобы полный проход шёл по узлам, а не одним списком на 100 000 камер.
- **`Clients/VmsFsCameraVideoStateSource.cs`** (новый, вместо `VmsFsCameraConnectionSource`) —
  группировка по узлу, пачки по 1000 камер, кешированный `FsServiceClient`; отсутствующий поток —
  `Unknown`, сбой узла — камеры этого узла `Unknown`.
- **`Clients/VmsOnvifReachabilityProbe.cs`** (новый) — `OnvifForServerAsync` (через новый
  `TryOnvifForServerAsync`: `null` = канала нет, исключение = `UNKNOWN`), кешированный клиент,
  deadline 6 с.
- **`Adapter/CameraPresenceRules.cs`** (новый) — чистые `CameraVideoSummary` и `Decide` (§10).
- **`Adapter/CameraPresenceService.cs`** (новый, scoped) — сводка, probe с параллельностью,
  `Decide`, запись.
- **`Adapter/CameraPresenceStore.cs`** (новый, scoped) — `ListNoVideoCameraIdsAsync` (join с
  `device_node`) и пакетное чтение текущих `CONNECTED`/`DISCONNECTED`/`NO_VIDEO` + `timestamp`
  для защиты от устаревшей записи. Проверить, что `StateService.UpdateStatesAsync` обновляет
  `object_states.timestamp` при каждой записи; если нет — дописать (столбец уже есть).
- **`Adapter/CameraStateWriter.cs`** — `ApplyPresenceAsync(cameraId, connected?, noVideo)` одной
  операцией; константа `NO_VIDEO`.
- **`Adapter/CameraPresenceSubscriber.cs`** (новый) — `camera-video-changed`, дедуплицирующий
  набор + дренаж раз в секунду (§11); payload — только адрес: берём из него `camera_id` (пустой —
  `Drop`) и больше ничего; после постановки в набор сообщение подтверждается, сбой обработки
  лечится sweep'ом.
- **`Adapter/CameraPresenceSweep.cs`** (новый) — старт + полный проход 5 мин + проход `NO_VIDEO`
  30 с; защита от устаревшей записи; пакетные RPC и записи.
- **`Adapter/DeviceEventSubscriber.cs`** — положительное событие: при `device-event` и
  `snapshot.Reachable == true` ставить камеру в тот же набор (третий канал; только вверх).
- **`Adapter/CameraChangeSubscriber.cs`** — после `EnsureCameraAsync` для названных камер звать
  `CameraPresenceService.EvaluateAsync` (правка потоков/выключения видна сразу).
- **Удаляются** `Adapter/ConnectionStatePoller.cs`, `Adapter/ConnectionStateService.cs`,
  `Clients/ICameraConnectionSource.cs`, `Clients/VmsFsCameraConnectionSource.cs`. Два теста
  `Reconcile…` **переносятся** в `DeviceNodeServiceTests` как тесты `EnsureCameraAsync`, а не
  удаляются.
- **`Program.cs`** — регистрация новых клиентов/сервисов/подписчика/sweep; удаление poller.

### Миграция и фронтенд

- **`Grpc/migrations/0006_no_video.sql`** — `NO_VIDEO` в `object_state_descriptions`
  (`alarm = false`, янтарный `#ffc107`, «No video») + частичный индекс
  `object_state_values (object_id) WHERE state = 'NO_VIDEO'`. Проверить `\d object_states` перед
  написанием.
- **`web_srv.client/src/map/nodeStyle.ts`** — приоритет: тревога → `present=false` (неисправность)
  → `NO_VIDEO` (янтарный из словаря) → первое известное состояние. `states[0]` при
  `CONNECTED`+`NO_VIDEO` даёт случайный цвет. `NodeStateChips` менять не нужно.
- Проверить, нет ли других потребителей `states[0]`: порядок из `StateService` не гарантирован.

### Документация

- **`docs/maps/states-and-alarms.md`** — `CONNECTED`/`DISCONNECTED` — присутствие по любому
  каналу (поток, ONVIF-ответ, ONVIF-событие), `NO_VIDEO` — отдельное нетревожное состояние
  «все потоки молчат»; обрывы VmsFs — не источник связи; падение video_srv — отказ узла, а не
  камер; MediaMTX-путь по-прежнему не сигнал.
- **`docs/future_plans/07-camera-connection-state.md`** → `legacy/` с записью о сделанном; строка
  из `docs/future_plans/README.md` удаляется.
- План — отдельным файлом `docs/plans/camera-connection-state.md` (этот текст), отметки «сделано»
  по ходу.

## Тесты

**VmsFs.Tests — `SourceVideoTrackerTests`** (чистые, без БД и Dapr):

- `AReportedFrameMakesTheStreamAlive`
- `AFirstDeadReportStaysUnknownUntilTheGrace`
- `ALostFrameMakesAnAliveStreamNoVideoImmediately`
- `ARestartedMediaServerStartsWithUnknownAndRecoversFromUnknown`
- `ANewRunThatStartsDeadBecomesNoVideoOnlyAfterTheGrace`
- `AnEmptyInstanceIdDoesNotRestartTheRun`
- `ASingleSilentStreamIsNoVideo`
- `AWholeSilentNodeSaysNothingAboutTheCameras`
- `ANeverHeardStreamIsNotListed`
- `AStaleEntryReadsAsUnknown`
- `PruneToForgetsOnlyRemovedStreams`
- `AnUnchangedReportRaisesNoNotification`

**VmsOnvif.Tests — `ReachabilityProbeTests`** (фейковый `ICameraConnectionSource` и `IReachabilityReader`):

- `ACameraWithoutOnvifIsNoOnvif`
- `AnUnresolvedCameraIsUnknown`
- `AnAnsweringOnvifIsReachable`
- `AnOnvifCallThatFailsButAcceptsTcpIsReachable` (401/SOAP Fault/таймаут + открытый порт)
- `AnOnvifCallThatFailsAndRefusesTcpIsUnreachable`
- `TheAnswerIsCachedAndSharedByConcurrentCallers`
- `CancellationPropagates`

**VmsMap.Tests — `CameraPresenceRulesTests`** (чистая таблица §3–§4):

- `AMainStreamDeadAndASubStreamUnknownIsNotNoVideo`
- `AMainStreamDeadAndASubStreamAliveIsVideoPresent`
- `AllConfiguredStreamsDeadIsNoVideo`
- `AReachableCameraWithNoVideoIsConnectedAndNoVideo`
- `AnUnreachableCameraWithNoVideoIsDisconnectedAndNoVideo`
- `ACameraWithoutOnvifAndAllStreamsDeadIsDisconnected`
- `ACameraWithoutOnvifAndAnUnknownStreamKeepsItsConnection`
- `NoProbeIsMadeWhenNothingIsKnownAboutTheStreams` (одни `Unknown` — не повод спрашивать ONVIF)
- `AProbeThatCouldNotBeMadeLeavesTheConnectionAlone`
- `AProbeAnsweredWith401StillMeansConnected` (исход `REACHABLE`)
- `ACameraWithoutStreamsIsConnectedByItsOnvifAnswer`
- `ACameraWithoutStreamsAndWithoutOnvifIsNotEvaluated`
- `ADisabledCameraOnlyClearsNoVideo`

**VmsMap.Tests — присутствие и sweep** (InMemory БД; Postgres при `VMS_MAP_TEST_DSN`):

- `ALiveStreamMakesTheCameraConnectedWithoutNoVideo`
- `ARecoveredStreamClearsNoVideo`
- `BothStatesAreWrittenInOneChange`
- `AnEventIsAnsweredFromTheSourceNotFromThePayload` (payload без состояния; источник говорит «мёртв»)
- `ACameraAbsentFromTheAnswerKeepsItsStates`
- `AnUnreadableCatalogKeepsTheStateAndIsHealedByTheSweep`
- `AReachableDeviceEventEnqueuesTheCamera` (положительный канал)
- `ASweepDoesNotOverwriteANewerDecision` (строка моложе начала наблюдения пропускается)
- `ASweepRestoresAStateWhoseEventWasLost`
- `AnUnchangedSweepWritesNothing`
- `ALargeBurstIsDeduplicatedAndAppliedWithBoundedParallelism`
- `ListNoVideoCameraIdsFindsOnlyCameras` (Postgres)
- Reconcile-тесты из `ConnectionStateServiceTests` → `DeviceNodeServiceTests.EnsureCameraAsync…`

**Фронтенд:** `nodeStyle` — `NO_VIDEO` янтарный и важнее `CONNECTED` в обоих порядках массива;
тревога важнее; `present=false` важнее `NO_VIDEO`.

**Нагрузка:** синтетический sweep-тест (много камер, состояния не меняются) — проверяет, что
записей и захватов блокировки нет; полный стенд 100 000 камер — `17-scale-bench-100k`, не здесь.

**Сборка:** `dotnet build dotnet/ta_vms.sln`, `dotnet test` по затронутым проектам, `npm test`,
`npm run build`/`lint`. C++ тестов нет — проверка живая.

## Живая проверка (OnvifSim)

1. Dev-стек + `onvifsim_lab.py up`/`provision`: у камер `CONNECTED`, чипа `NO_VIDEO` нет.
2. **Пропало только видео:** подменить `path` одного потока на `rtsp://127.0.0.1:9/unreachable` и
   записать камеру через `POST /api/live/cameras`. За ~10–15 с: `NO_VIDEO` (янтарный чип), связь
   `CONNECTED`, тревог нет. Вернуть путь → `NO_VIDEO` снимается. (Полный `set_network(False)` для
   этого пункта не годится — рубит и ONVIF.)
3. **Второй поток неизвестен:** у камеры с двумя потоками сломать один и не трогать второй —
   `NO_VIDEO` не появляется, пока второй жив/неизвестен; гибель обоих даёт `NO_VIDEO`.
4. **Камера пропала целиком:** `sim.set_network(cam, False)` → `NO_VIDEO` + `DISCONNECTED`
   (красный, счётчики/пространства) примерно за 10–15 с. Включить обратно → `CONNECTED`.
5. **Без записи:** профиль `off`; повторить п. 4 — открытой строки `source_outages` нет, а карта
   краснеет. Доказательство, что `recording_expected` больше ни при чём.
6. **С записью:** `onvifsim_lab.py edge`; повтор обрыва — строка источника появляется и
   восстанавливается как раньше (регрессия архивных обрывов).
7. **Неверные учётные данные ONVIF:** испортить пароль ONVIF при живом видео → `CONNECTED`;
   сломать путь потока → `CONNECTED`+`NO_VIDEO`, probe обязан ответить `REACHABLE` (TCP/401), а
   не `UNREACHABLE`.
8. **Камера без ONVIF** (file-камера или конфигурация без `onvif_conn`): погасить поток →
   `DISCONNECTED`+`NO_VIDEO`, probe — `NO_ONVIF`. Камера вовсе без потоков и без ONVIF —
   состояние не меняется.
9. **Перезапуск VmsFs:** состояния не меняются, после отчётов переходы публикуются, лишних
   тревог нет. **VmsFs + video_srv одновременно:** без вспышки красного; камеры восстанавливаются
   по первому кадру.
10. **Перезапуск video_srv:** ни одна камера не вспыхивает `NO_VIDEO`/`DISCONNECTED`; камеры с
    живым видео возвращаются за ~1 с после кадра.
11. В логах VmsOnvif — probe только по камерам с известной проблемой видео; в логе симулятора —
    лишних ONVIF-запросов по камерам с живым видео нет; в логах карты — одна запись на
    изменившуюся камеру, sweep без изменений не пишет.
12. Погасить стек, симулятор и Vite; ничего не оставлять запущенным.

## Шаги (по коммиту на шаг)

1. **План** — `docs/plans/camera-connection-state.md`; ветка режется здесь.
2. **proto** — `ListSourceStates` и `ProbeReachability`; сборка.
3. **VmsFs** — `SourceVideoTracker`, notifier, wiring в handler/sweeper, DI, тесты трекера.
4. **video_srv** — немедленный отчёт на переходе.
5. **VmsOnvif** — `ReachabilityProbe`, `AnswersAsync`, RPC, DI, тесты.
6. **VmsMap, клиенты** — `CameraInfo`, `ServerIdsAsync`, видео-источник, probe-клиент; тесты.
7. **VmsMap, присутствие** — миграция `0006`, `CameraStateWriter.ApplyPresenceAsync`,
   `CameraPresenceRules/Service/Store/Subscriber/Sweep`, хук `DeviceEventSubscriber` и
   `CameraChangeSubscriber`, удаление poller/service/source, перенос Reconcile-тестов, DI, тесты.
8. **Фронтенд** — `nodeStyle` + тесты.
9. **Документация** — `states-and-alarms.md`, future plan → legacy, README, отметки в плане.
10. **Живая проверка** — шаги выше, отметки в плане.

Push и merge в `master` — только по просьбе.

## Ход работы

**2026-11-23.**

- ☑ Шаг 1: план в репозитории (`Docs: plan the camera presence rework`).
- ☑ Шаг 2: proto — `ListSourceStates` и `ProbeReachability`; сборка решения 0/0.
- ☑ Шаг 3: VmsFs — `SourceVideoTracker`, `SourceVideoNotifier`, wiring в handler и sweeper.
  Уточнено правило чтения: устаревший `Alive` отдаётся как `Unknown`, а `NoVideo` от тишины
  сохраняется — иначе выведенный sweep'ом `NO_VIDEO` скрывался бы собственным чтением.
  `VmsFs.Tests` — 18 зелёных.
- ☑ Шаг 4: video_srv — карта последнего отчёта и немедленный `send_liveness()` при смене
  `source_alive` или набора потоков; сборка `vms` 0/0.
- ☑ Шаг 5: VmsOnvif — `CameraReachabilityProbe`: ONVIF-ответ, затем TCP-connect (401/SOAP Fault
  доказывают присутствие), кеш 10 с и single-flight; `NO_ONVIF` отдельно от `UNREACHABLE`.
  Enum в proto назван `Reachability`, чтобы сгенерированные имена были `Unknown`/`Reachable`/…
  `VmsOnvif.Tests` — 60 зелёных.
- ☑ Шаг 6: VmsMap, клиенты — `CameraInfo` с потоками/`Disabled`/`HasOnvif`,
  `IMediaServerRegistry.ServerIdsAsync`/`TryOnvifForServerAsync`, `VmsFsCameraVideoStateSource`
  (пачки по 1000), `VmsOnvifReachabilityProbe`; `CameraCatalogTests` — 6.
- ☑ Шаг 7: VmsMap, присутствие — `0006_no_video.sql`; `CameraPresenceRules` (чистая таблица),
  `CameraPresenceService`/`Store`/`Queue`/`Subscriber`/`Sweep`; `ApplyPresenceAsync`;
  положительный хук `device-event`/`snapshot.reachable`; очередь из `CameraChangeSubscriber`;
  удалены `ConnectionStatePoller`/`ConnectionStateService`/`ICameraConnectionSource`/
  `VmsFsCameraConnectionSource`. `ConnectionStateServiceTests` удалён (обе Reconcile-проверки уже
  покрыты `DeviceNodeServiceTests`). Найдено тестом и исправлено: выключенная камера больше не
  опрашивает ONVIF. `VmsMap.Tests` — 495, решение — 0/0.
- ☑ Шаг 8: фронтенд — `nodeStyle` выбирает `NO_VIDEO` по имени (порядок набора не гарантирован),
  тревога и неисправность остаются важнее; `npm test` — 130, `npm run build`/`lint` — чисто.

## Не входит

- Архивные обрывы и `recording_expected` — не меняются.
- Пакетная запись состояний одним заходом (сейчас — предварительный diff и запись по камере;
  решение — по нагрузочному стенду).
- Метрики задержки и очередей: в проекте нет системы метрик; диагностика — логи.
- `device_node.reachable` и его отображение в UI неисправностей.
- WS-Discovery/ICMP как отдельный канал: ONVIF-ответ + TCP-connect покрывают «камера в сети».
- Частота discovery-sweep VmsOnvif (`DEVICE_DISCOVERY_INTERVAL_SECONDS`) не меняется.
- `NO_VIDEO` как тревожное состояние — включается строкой в словаре, сегодня решение «нет».
