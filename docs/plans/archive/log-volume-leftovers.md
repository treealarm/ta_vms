# Хвосты объёма логов

> Ветка `fix/log-volume-leftovers` от `master` (7854160f). Исходник —
> `docs/future_plans/03-log-volume-leftovers.md` (удалён вместе с закрытием).
> Стенд — OnvifSim (`tests/onvifsim/`) и dev-стек, поднятый `scripts/dev-up.sh`.
> **Закрыт 2026-11-18.**

## Сделано

- **A. CameraActor** (2026-11-18): окно 5 с, `CameraUnavailableBackoff`, тесты
  `VmsOnvifActors.Tests` (3 зелёных), проект в solution. На стенде: 40 вызовов
  `/api/ptz/status` к выключенной камере — 8 попыток `GetServices` (одна на окно) и все ответы
  быстрее 50 мс; до правки каждая из 40 попыток шла в камеру.
- **B. video_srv** (2026-11-18): `log_connect_failure`/`reset_connect_failure_log`, три
  connect-фазовых Error, номер попытки, потолок 5 минут; `vms` собирается. На стенде строки
  пришли на попытках 1, 2, 4, 8, 16, 32 через 5, 10, 20, 40, 80, 160 с; после возврата камеры
  `RTSP connected` — через 5 с, переподключение не замедлилось.
- **C. web_srv** (2026-11-18): `AddVmsLogging`, ссылка на VmsLog, `APP_ID`/`VMS_LOG_PATH` в
  launch.json, `APP_ID` и `data`-том в dev- и `../ta_install` compose; `dotnet build` и
  `docker compose config` проходят. На стенде пишется `logs/web-vms/web-vms.log`, а
  `/api/logger/sources` перечисляет `web-vms`.
- **D. Debug** (2026-11-18): до правки `VmsOnvif.OnvifEventSubscriptionService` давал 146 строк
  Debug за 5 минут — 51% всех Debug в системе (следующий источник — 63). Построчные сообщения о
  событиях переведены в Trace; в следующем пятиминутном окне из этого источника 0 строк (всего
  Debug — 5, все от `DeviceIoDiscoveryService`), при этом события по-прежнему доходят —
  `/api/onvif/cameras/{id}/recent-events` их отдаёт.
- **Попутно** (2026-11-18): найдены и записаны `docs/future_plans/26-ptz-move-stack-traces.md` и
  `27-onvif-pull-point-retry-log.md` — стек на каждую PTZ-команду к выключенной камере и
  трёхстрочный ретрай pull point в OnvifLib каждые 2 секунды.
- **Проверки**: `dotnet build dotnet/ta_vms.sln` — 0 ошибок и предупреждений,
  `dotnet test dotnet/ta_vms.sln` — 703 из 703, `cmake --build video_srv/build` — успешно.

## Что измерено (2026-10-25, чистая Windows-установка)

- Выключенная камера: `CameraActor` сбрасывает закэшированный `Camera` при неудачном
  разрешении сервисов, поэтому каждый следующий вызов снова идёт в `GetServices` — около
  четырёх попыток и трёх секунд на один запрос PTZ-статуса, каждая попытка — строка Error.
- `video_srv`: одна строка `Failed to open RTSP` на поток примерно раз в 5 с, пока камера
  выключена: 69 КБ за пять минут на четыре камеры, около 20 МБ в сутки.
- `web_srv` не подключён к `AddVmsLogging`: нет файла под `VMS_LOG_PATH` и нет источника
  `web-vms` во вкладке Logs (VmsLogger перечисляет подпапки `VMS_LOG_PATH`).
- Debug-строки сервисов после удаления SOAP-дампов большими не были, но это следующее, на что
  смотреть.

## Решения

- **Отрицательный кэш — в актёре, не в OnvifLib.** Изменение в библиотеке потребовало бы
  релиза пакета и синхронного бампа в трёх репозиториях; локальное окно решает измеренную
  проблему. Окно 5 с: камера, вернувшаяся в сеть, снова обслуживается в пределах одного
  watchdog-интервала, а вызывающий с частотой 1 Гц получает один поход в `GetServices` на
  окно вместо одного на вызов.
- **Кэш входится только на «камера не отвечает».** Недоступность — это `null` от
  `Camera.GetServicesAsync()` (разрешение сервисов провалилось) и `null` от
  `PtzService2.GetStatusAsync()` (камера перестала отвечать уже после разрешения). «PTZ-сервиса
  нет» (карта сервисов есть, но без PTZ) и пустой снимок — не недоступность: их кэшировать
  нельзя, иначе камера без PTZ или с одним неудачным кадром выпадала бы из работы на окно.
- **Переподключение в `video_srv` не меняется.** Меняется только момент записи строки: первая
  неудача пишется сразу, дальше — каждый раз, когда интервал с предыдущей записи удвоился
  (5, 10, 20, 40, 80, 160, 300 с — потолок 5 минут). Попытки открыть RTSP идут по-прежнему
  каждые `kWatchdogInterval` (5 с).
- **`web_srv` получает `APP_ID = web-vms`** — это его runtime-имя (Dapr app id; `web_vms` —
  только имя compose-сервиса). Так папка лога и источник во вкладке Logs называются `web-vms`,
  как остальные сервисы называются своими app id. На Windows `APP_ID` подставляет
  `TaVmsHost` (`StackConfig.cs`: `env["APP_ID"] = a.AppId`), отдельной правки манифеста не нужно.
- **Debug не трогаем до измерения.** После частей A–C на стенде считается, что именно пишется на
  Debug; код меняется только там, где источник действительно доминирует.

## Часть A. CameraActor — короткое окно недоступности

### A1. Окно

Новый файл `dotnet/VmsOnvifActors/CameraUnavailableBackoff.cs` — маленький класс без
зависимостей от Dapr:

```csharp
public sealed class CameraUnavailableBackoff(TimeSpan window, TimeProvider time)
{
  private string? _cameraKey;
  private DateTimeOffset _retryAt;

  public bool IsBackedOff(string cameraKey) =>
    cameraKey == _cameraKey && time.GetUtcNow() < _retryAt;

  public void Note(string cameraKey)
  {
    _cameraKey = cameraKey;
    _retryAt = time.GetUtcNow() + window;
  }
}
```

Строгое `<` в `IsBackedOff`: ровно в момент истечения окна камера снова опрашивается.

`dotnet/VmsOnvifActors/Program.cs`: `builder.Services.AddSingleton(TimeProvider.System);`.
`CameraActor` получает `TimeProvider time` в конструктор и держит
`new CameraUnavailableBackoff(TimeSpan.FromSeconds(5), time)`.

### A2. Точки в актёре

`dotnet/VmsOnvifActors/CameraActor.cs`:

1. `CameraKey(conn)` — вынести существующее построение ключа из `EnsureCamera` в статический
   хелпер; `EnsureCamera(conn, key)` принимает готовый ключ. Ключ содержит пароль — он остаётся
   внутренним, в лог не попадает.
2. Каждый публичный метод начинается с
   `if (_backoff.IsBackedOff(key)) return <пустой ответ>;`.
3. `NoteServiceFailure(key)` = `_backoff.Note(key); Evict();`. Вызывается ровно в этих местах:

| метод | когда |
|---|---|
| `GetProfilesAsync`, `GetStreamUriAsync`, `GetSnapshotAsync` | `GetMediaService()` вернул `null` |
| `GetPtzCapabilitiesAsync`, `GetPtzStatusAsync`, `GetPtzOrThrow` | `GetServicesAsync()` вернул `null` (до `GetPtzService()`) |
| `GetPtzStatusAsync` | `ptz.GetStatusAsync()` вернул `null` — камера умерла после разрешения |

`GetPtzCapabilitiesAsync` и `GetPtzStatusAsync` дополнительно проверяют
`GetServicesAsync() is null` **до** `GetPtzService()`, чтобы отличить «не отвечает» от «PTZ не
поддерживается». Пустой ответ `GetSnapshotAsync` (`image == null`) окно не открывает — как и
сейчас, один неудачный кадр не означает мёртвую камеру.

Отдельной строки в лог актёр не пишет: единственная строка на окно — существующий Error от
OnvifLib (`ONVIF GetServices failed ...`), а повторные вызовы внутри окна до неё не доходят.
Это и есть «залогировал один раз» без дублирующего сообщения.

### A3. Кто опрашивает PTZ-статус раз в секунду

В текущем дереве периодического вызова нет: `/api/ptz/status` дёргают только два операторских
места (`CameraSlaveBindingSection.tsx`, `PtzGeoCalibrationDialog.tsx`, оба — по нажатию) и
лабораторный `tests/onvifsim/vmsapi.py`. Поиск по истории git опроса тоже не находит. Значит,
источник был вне ta_vms (старый внешний клиент или ручной цикл на той машине); установить его
по сохранившимся логам нельзя — web_srv не логировал запросы. Проверка на стенде (A4) делает
вопрос неважным: при вызове 1 Гц число строк не зависит от частоты вызывающего.

### A4. Тесты

Новый проект `dotnet/VmsOnvifActors.Tests` (csproj — копия `VmsOnvif.Tests`, ссылка на
`VmsOnvifActors`), добавить в `dotnet/ta_vms.sln` (Windows CI гоняет `dotnet test` по solution).
`CameraUnavailableBackoffTests`:

- отмеченная камера недоступна внутри окна и снова доступна ровно по его истечении;
- другая камера (другой ключ) не задета;
- повторное `Note` продлевает окно от момента повтора.

Время — ручной `FakeTime : TimeProvider` по образцу `VmsOnvif.Tests/DeviceIoStateServiceTests.cs`.

## Часть B. video_srv — редеющая запись неудачных открытий

`video_srv/reader/rtsp_reader.{h,cpp}`. Состояние — обычные члены (их трогает только поток
чтения):

```cpp
std::int64_t m_connect_failures = 0;
std::chrono::seconds m_connect_failure_log_interval;   // начинается с kWatchdogInterval
std::chrono::steady_clock::time_point m_next_connect_failure_log_at{};
```

В анонимном пространстве `.cpp` рядом с `kWatchdogInterval`:

- `kMaxConnectFailureLogInterval = std::chrono::minutes(5)`;
- `log_connect_failure(const std::string& message)` — `++m_connect_failures`; если
  `now < m_next_connect_failure_log_at`, тихо вернуться; иначе
  `logger::instance().error(message + " (attempt N)")`, затем
  `m_next_connect_failure_log_at = now + m_connect_failure_log_interval` и
  `m_connect_failure_log_interval = std::min(m_connect_failure_log_interval * 2, cap)`;
- `reset_connect_failure_log()` — сброс счётчика и интервала к начальным значениям.

Вызовы: все три connect-фазовых Error в `connect()` — `Failed to open RTSP`,
`Failed to get stream info`, `No video stream`. Успех — единственная точка перед
`return true` (`RTSP connected`), там же `reset_connect_failure_log()`. `open()` дополнительно
сбрасывает состояние, потому что может прийти новый URL.

Поток: попытки каждые 5 с, строки — на 1-й, 2-й, 4-й, 8-й, 16-й, 32-й… неудаче (5, 10, 20, 40,
80, 160 с, дальше раз в 5 минут). За пять минут на камеру 6 строк вместо 60.

## Часть C. web_srv — собственный лог и вкладка Logs

1. `dotnet/web_srv.Server/web_srv.Server.csproj` — явная ссылка `..\VmsLog\VmsLog.csproj`
   (сейчас VmsLog приходит транзитивно через VmsCfg; Dockerfile уже копирует проект).
2. `dotnet/web_srv.Server/Program.cs` — `using VmsLog;` и `builder.AddVmsLogging();` сразу после
   создания builder, до первого логгера.
3. `dotnet/web_srv.Server/appsettings.json` — удалить секцию `Logging`: провайдеры очищаются,
   уровни задаёт VmsLog, и оставленная секция только вводит в заблуждение.
4. `APP_ID`:
   - `.vscode/launch.json`, конфигурация «Backend (.NET)»: `"APP_ID": "web-vms"` и
     `"VMS_LOG_PATH": "${workspaceFolder}/../logs"`;
   - `docker-compose.yml` (dev), сервис `web_vms`: `environment: APP_ID: web-vms` и
     `volumes: *data-volume` — без общего тома `web-vms` не виден VmsLogger;
   - `../ta_install/docker-compose.yml`, сервис `web_vms`: то же самое;
   - `../ta_install/docker-compose.distributed.yml` уже задаёт `APP_ID` и монтирует `./data:/data`
     через `x-app` — правок нет;
   - Windows: `TaVmsHost` подставляет `APP_ID` из манифеста, `VMS_LOG_PATH` уже в
     `windows.env` — правок нет;
   - `.env` не трогаем: `APP_ID` — переменная конкретного сервиса, а `VMS_LOG_PATH` там уже есть.

Ожидаемый результат: файл `logs/web-vms/web-vms.log`, источник `web-vms` во вкладке Logs,
`Microsoft.*` на Warning (как у остальных через VmsLog).

## Часть D. Debug — измерено и решено

Пятиминутное окно на стенде с камерой, шлющей события, показало доминанту:
`VmsOnvif/OnvifEventSubscriptionService.cs` — 146 строк Debug, 51% всех Debug в системе
(следующий источник — 63). Это 2–3 строки на каждое событие («received», «checking N
filter(s)», «no filter matched»); события видны во вкладке ONVIF events, поэтому строки
переведены в Trace — VmsLog держит `MinimumLevel.Debug`, и Trace не пишется. Контрольное окно:
0 строк из этого источника, события по-прежнему доходят до `recent-events`.

## Проверки

1. `dotnet build dotnet/ta_vms.sln`, `dotnet test dotnet/VmsOnvifActors.Tests` (и полный
   `dotnet test dotnet/ta_vms.sln` перед merge).
2. `cmake --build video_srv/build -j"$(nproc)"` (пресет `linux-default`, Ninja).
3. Стенд: `docker compose up -d postgres rabbitmq mediamtx db-init`, `scripts/dev-up.sh --build`,
   `python3 tests/onvifsim/onvifsim_lab.py up` + `provision`.
4. **A**: выключить одну камеру в симуляторе (`PUT /api/cameras/{id}/network {"online":false}`),
   дёргать `/api/ptz/status` раз в секунду 60 с. В
   `logs/vmsonvif-actors/vmsonvif-actors.log` — около 12 строк `ONVIF GetServices failed` вместо
   ~60; `/api/ptz/status` отвечает сразу, без трёхсекундных пауз.
5. **B**: уронить RTSP-источник (`ffmpeg` в MediaMTX убить) и выждать 5 минут. В
   `logs/vms/vms.log` — 7 строк `Failed to open RTSP` на камеру с номерами попыток
   1, 2, 4, 8, 16, 32, 64 и метками через 5, 10, 20, 40, 80, 160 с — видно, что переподключение
   по-прежнему каждые 5 с. После подъёма источника — снова `RTSP connected`, счётчик обнулён.
6. **C**: после перезапуска `web_vms` существует `logs/web-vms/web-vms.log`; вкладка Logs
   перечисляет `web-vms` и показывает записи; `Microsoft.AspNetCore` не заливает файл.
7. **D**: отчёт по объёму Debug за окна измерения; при правке — повторное измерение.

## Коммиты и закрытие

- `Give CameraActor a short unavailable window` (+ тесты);
- `Back off RTSP open failures in the log`;
- `Log web_srv through VmsLog` (`ta_vms`), отдельным коммитом в `../ta_install` — compose;
- при необходимости `Quiet per-event ONVIF Debug logging`;
- `Close the log-volume leftovers`: удалить `docs/future_plans/03-log-volume-leftovers.md` и его
  строку из `future_plans/README.md`.

`../ta_install` получает ветку с тем же именем. Push и merge в `master` — только по просьбе.

## Что сознательно не делаем

- Не меняем OnvifLib (релиз пакета и бамп в трёх репозиториях ради того, что решается в актёре).
- Не меняем интервал переподключения `video_srv` и не трогаем watchdog.
- Не добавляем `APP_ID` в общий `.env`.
- Не трогаем Debug без измерения.
