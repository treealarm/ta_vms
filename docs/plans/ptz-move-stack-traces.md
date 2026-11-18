# PTZ-команда к выключенной камере: результат вместо исключения

> Ветка `fix/ptz-move-stack-traces` от `master` (1502760f). Исходник —
> `docs/future_plans/26-ptz-move-stack-traces.md` (удаляется при закрытии).
> Стенд — OnvifSim (`tests/onvifsim/`, `Sim.set_network`) и dev-стек, поднятый
> `scripts/dev-up.sh`.

## Что видно (замер до правки)

Демо-мир Maryland, пара `cam1` (master) → `cam2` (slave, ONVIF-порт 8102), сессия трекера
идёт, `ptz` снят с сети через `PUT /api/cameras/ptz/network {"online": false}`. Считаются
`logs/vmsonvif-actors/vmsonvif-actors.log` и
`logs/vmsptztrackingsingleton/vmsptztrackingsingleton.log` (`VMS_LOG_PATH` = `/mnt/wdblue/tests/logs`).

| случай | окно | стеков | текст |
|---|---|---|---|
| **тёплый актёр** — камера вышла из сети после того, как актёр разрешил сервисы | 150 с | **69** (по одному на команду, ~2,1 с) | `CommunicationException ... response ended prematurely`, 4,6 КБ |
| **холодный актёр** — хост перезапущен при выключенной камере | 60 с | **28** | `InvalidOperationException: PTZ service not available for 127.0.0.1:8102`, ~1,5 КБ |

Прошлый замер (в future plan) — 41 стек за ~2,5 минуты в холодном случае; сегодня он же дал
28 за 60 с, потому что стек пишется на **каждую** команду трекера, а не на окно.

Стеки и текст ошибки возникают в двух местах:

1. **Хост актёра.** Метод актёра, бросивший исключение, — для Dapr это неудачный HTTP-запрос;
   в dev его логирует `Microsoft.AspNetCore.Diagnostics.DeveloperExceptionPageMiddleware`
   («An unhandled exception has occurred while executing the request») со стеком, в
   production — конвейер Kestrel. 69 × 4,6 КБ за 150 с.
2. **Трекер.** Dapr-клиент кладёт всё тело ответа 500 в ошибку вызывающего;
   `PtzCommandSender` ловит `RpcException`, а `SlaveAimingSession.Fail` пишет «cannot aim — …».
   В тексте — удалённый стек и заголовки ответа (в `traceparent` каждый раз новый), поэтому
   дедупликация в `Fail` (`_lastError == error`) не срабатывает никогда: 102 строки / 422 КБ
   за ~4 минуты, в среднем 4,1 КБ на строку.

Итого около 8 КБ на команду; при темпе ~2,1 с это ~41 тыс. команд в сутки, то есть **сотни
мегабайт в сутки на пару «ведущая/ведомая»** — больше, чем future plan оценивал по одному файлу.

Почему два пути:

- **Холодный:** `CameraActor.GetPtzOrThrow` бросает. Большая часть — ветка бэкоффа (5 с), то
  есть каждая команда внутри окна бросает сразу, не доходя до камеры.
- **Тёплый:** `Camera.GetServicesAsync()` кэширует удачно разрешённые сервисы, поэтому
  `GetPtzService()` возвращает объект, а падает сам вызов `AbsoluteMoveAsync` внутри OnvifLib:
  `PtzService2` не ловит исключения в move/stop (в отличие от `GetStatusAsync`, который ловит и
  возвращает `null`). Каждая команда доходит до сети и бросает снова.

## Решение

Актёр не бросает на ожидаемое «камеры нет» — он **возвращает результат**, как уже сделано для
статуса (`null`).

### Контракт (`dotnet/web_srv.Types/Actors/ICameraActor.cs`)

```csharp
/// The outcome of a PTZ command. A camera that cannot be reached is an ordinary state of the
/// network, and a method that throws is reported by the Dapr host as an unhandled exception with
/// its stack on every call — so the command reports failure as a value, as the status read does.
public record PtzCommandActorResult(bool Success, string? Error = null);
```

Четыре метода `ICameraActor` меняют сигнатуру на `Task<PtzCommandActorResult>`:
`PtzContinuousMoveAsync`, `PtzAbsoluteMoveAsync`, `PtzRelativeMoveAsync`, `PtzStopAsync`.
`web_srv.Types` — общий проект; `PtzServiceImpl` (VmsOnvif) и `CameraActor`
(VmsOnvifActors) — единственные потребители этих методов (проверено поиском
`InvokeMethodAsync`).

### `CameraActor`

1. `GetPtzOrThrow` → `FindPtzAsync(conn, key)`: возвращает `PtzService2?`. Три прежних
   `throw` становятся `return null`; бэкофф не продлевается, `NoteServiceFailure` — только
   когда `GetServicesAsync()` вернул `null` (как сейчас; «PTZ-сервиса нет у ответившей камеры»
   недоступностью не считается).
2. Общее тело четырёх команд:

```csharp
private async Task<PtzCommandActorResult> CommandAsync(
  CameraConnInfo conn, Func<PtzService2, Task> command)
{
  var key = CameraKey(conn);
  var ptz = await FindPtzAsync(conn, key);
  if (ptz is null)
    return new PtzCommandActorResult(false, $"PTZ service not available for {conn.Ip}:{conn.Port}");

  try
  {
    await command(ptz);
    return new PtzCommandActorResult(true);
  }
  catch (Exception ex)
  {
    // The camera stopped answering between discovery and the command, or refused it. This is the
    // warm-actor half of an outage: without the note every command would go to the dead camera
    // again, and the caller would receive an exception the host logs with a stack.
    NoteServiceFailure(key);
    return new PtzCommandActorResult(false, $"{ex.GetType().Name}: {ex.Message}");
  }
}
```

   Четыре метода становятся однострочными обёртками:
   `PtzAbsoluteMoveAsync` → `CommandAsync(request.Conn, ptz => ptz.AbsoluteMoveAsync(...))` и т. д.
   Текст «PTZ service not available for ip:port» сохраняется — он уже виден оператору.
3. `GetPtzStatusAsync` и `GetPtzCapabilitiesAsync` переводятся на `FindPtzAsync` (поведение то
   же: `null` → `null`/пустой DTO). Это убирает три копии одной и той же последовательности
   «бэкофф → GetServices → GetPtzService».
4. `EnsureCamera` продолжает бросать (`Camera belongs to node ...`) — это ошибка развёртывания,
   а не состояние камеры.

### `PtzServiceImpl`

Четыре RPC (`ContinuousMove`, `AbsoluteMove`, `RelativeMove`, `Stop`) вызывают
`InvokeMethodAsync<TRequest, PtzCommandActorResult>` и отображают результат:

```csharp
private static PtzResponse From(PtzCommandActorResult result) =>
  result.Success ? Ok() : Fail(result.Error ?? "The camera did not accept the PTZ command");
```

`catch (Exception ex) → Fail(ex.Message)` остаётся для всего неожиданного (дедлайн 2 с,
недоступный хост актёра, несовпадение узла). Proto не меняется: `PtzResponse.Success/Error`
уже несёт ответ, и вызывающие (трекер, джойстик оператора) получают ровно то же, что получали
из прежнего `catch`.

## Поведенческие следствия (намеренные)

- **Неудачная команда открывает 5-секундное окно.** В простое камера опрашивается раз в окно,
  а не на каждую команду; у тёплого актёра это ещё и снимает очередь: при камере, которая
  отбрасывает пакеты (а не отказывает в соединении), ONVIF-вызов висит 15 с, и без `Note`
  команды копились бы друг за другом.
- **Текст ошибки становится коротким и стабильным** — `SlaveAimingSession.Fail` начинает
  дедуплицировать, и на весь простой остаётся одна строка «cannot aim» вместо строки на
  команду.
- **Трекер и API видят то же поведение, что и раньше:** `PtzServiceImpl` и сейчас превращал эти
  исключения в `PtzResponse{Success=false, Error=…}`; меняется только качество текста
  (`lastError` в `/api/ptz-tracking` становится «PTZ service not available for 127.0.0.1:8102»
  вместо 5 КБ удалённого стека).
- **Симптоматическое лечение отклонено** (фильтр лога без стека): стек всё равно уезжает в тело
  ответа и пишется трекером, а для актёра каждая команда остаётся необработанным исключением.

## Тесты

`dotnet/VmsOnvifActors.Tests/CameraActorPtzCommandTests.cs`:

- `ActorHost.CreateForTest<CameraActor>()`, `CameraActorDomain("vms")`,
  `new OnvifLoggerAdapter(NullLogger.Instance)`, `TimeProvider.System`.
- Камера-заглушка — `127.0.0.1:1` (отказ в соединении быстрый и детерминированный, без сети и
  без OnvifSim).
- Контракт: `PtzAbsoluteMoveAsync`, `PtzContinuousMoveAsync`, `PtzRelativeMoveAsync`,
  `PtzStopAsync` к недостижимой камере возвращают `Success == false`, не бросают, и `Error`
  называет адрес.
- Повторный вызов внутри окна бэкоффа возвращает тот же отказ (тот же текст), не бросая.

Тёплый путь (исключение из самого ONVIF-вызова) юнит-тестом не покрывается — для него нужен
объект `PtzService2` с живым каналом; он проверяется на стенде (ниже). Код там — шесть строк
`try/catch`.

## Стенд: проверка после правки

1. Сборка и тесты (см. «Проверки»), затем
   `scripts/dev_stack.py restart --build vmsonvif-actors vmsonvif` — в запущенном стеке должна
   быть новая сборка (`restart` без `--build` её не берёт).
2. **Тёплый случай.** Walker — в маршрут холла (`Room 101`), трекер ведёт `cam2`; убедиться, что
   команды идут (`/api/ptz-tracking` — `target.kind == "person"`, поза `ptz` меняется). Снять
   `ptz` с сети на 150 с. Ожидается:
   - в `vmsonvif-actors.log` — **0** новых «unhandled exception»;
   - в `vmsptztrackingsingleton.log` — одна строка «cannot aim», не 69;
   - `lastError` в `/api/ptz-tracking` — «PTZ service not available for 127.0.0.1:8102»;
   - в логе актёра остаются только Error-строки OnvifLib о неудачной попытке `GetServices` —
     примерно одна на 5-секундное окно.
3. **Холодный случай.** Оставив `ptz` выключенной, перезапустить `vmsonvif-actors`; 60 с.
   Ожидается 0 стеков и тот же короткий `lastError`.
4. **Восстановление.** Вернуть `ptz` в сеть (`online: true`): в пределах окна (~5 с) трекер
   снова ведёт цель, `lastError` пуст, поза меняется.
5. Стек остановить, камеру оставить в сети.

## Проверки

1. `dotnet build dotnet/ta_vms.sln` — 0 ошибок и предупреждений.
2. `dotnet test dotnet/VmsOnvifActors.Tests`; перед merge — полный `dotnet test dotnet/ta_vms.sln`.
3. Стенд по шагам выше; замеры «до» (69/150 с тёплый, 28/60 с холодный) записаны в этот файл,
   «после» — вписываются при закрытии.

## Коммиты и закрытие

- `Return a PTZ command result instead of throwing in CameraActor` (+ тесты);
- `Close the PTZ move stack traces` — удалить `docs/future_plans/26-ptz-move-stack-traces.md` и
  его строку из `docs/future_plans/README.md`.

Push и merge в `master` — только по просьбе.

## Что сознательно не делаем

- **Не трогаем OnvifLib.** Move/stop там продолжают бросать: это контракт библиотеки с
  устройством, а не с нашим хостом актёра; правка потребовала бы релиза пакета и бампа в трёх
  репозиториях.
- **Не трогаем `PtzCommandSender`.** Он ловит `RpcException` — после правки при выключенной
  камере RpcException не будет (ответ обычный, `Success=false`), и его Warning со стеком тоже
  исчезнет.
- **Не добавляем поле в proto.** `PtzResponse.Success/Error` уже есть.
- **Не подавляем логирование в хосте актёра.** Лечится контракт, а не фильтр.
