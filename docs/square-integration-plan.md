# Интеграция ta_vms в Square: typelib-клиент + VmsCfg как producer

> Статус: **план на будущее, не реализован.** Документ — ориентир для развития видеоподсистемы.
> Текущая разработка не должна противоречить этому плану (см. раздел «Square integration (future)»
> в `CLAUDE.md`). Саму интеграцию выполняем позже, отдельной задачей.

## Context

`/mnt/wdblue/tests/ta_vms` — распределённая VMS (запись/стриминг на базе MediaMTX).
Square (соседний проект `../Square`) — основная система мониторинга объектов на карте (Leaflet).
Родственные проекты одного вендора с почти идентичным стеком: **Dapr + Redis (pub/sub) +
PostgreSQL + .NET 8 + gRPC + React**. Нужно встроить ta_vms в Square так, чтобы ta_vms
**по-прежнему запускался полностью автономно**.

Согласованные решения:
- **Глубина:** поэтапно, в итоге всё (события/команды → видео в UI → единый вход).
- **Конфиг/камеры:** ta_vms — владелец функционального конфига; конфиг **пушится снизу вверх**
  (ta_vms → Square), в Square — read-only. Square владеет «презентацией» (позиция/имя/группировка).
  Менять состояние из Square — **только командами**.
- **Инфраструктура:** общая шина Redis (pub/sub) + общая docker-сеть, **раздельные PostgreSQL**.
- **Форма слоя:** Square — хост интеграции с расширяемым **gRPC + Dapr** API (+ REST). Producer —
  переиспользуемый **typelib-клиент** (аналог `IntegrationUtilsLib`, но со стороны отдающего),
  который подключает `VmsCfg`. Протокол — **напрямую протокол Square** (не нейтральный).
- **Связанность:** Square свободно перерабатываем; ta_vms — трогаем **только `VmsCfg`** (подключение
  typelib), а автономность сохраняем **гейтом по env** (нет sink'а Square → ведёт себя как раньше).
  Тяжёлое ядро (C++ media_server и пр.) не трогаем.
- **Модель объекта:** root-объект = **Dapr APP_ID** процесса (`vmscfg`); дети — камеры. Дерево
  `vms-сервер(APP_ID) → камера`. Свойства самоописывающиеся, read-only. Команды — типизированные
  actions.
- **Жизненный цикл:** удаление root в Square → **каскадное удаление детей + разрыв интеграции**;
  отдельные объекты можно **выключить из интеграции в админ-режиме** (per-object disable).
- **Команды/триггеры:** через **Dapr service invocation на APP_ID, асинхронно** — команда
  возвращает handle (uid) сразу, статус прилетает позже (как `ProtoActionExe.uid` +
  `UpdateActionResults` в `Square/Protos/integro.proto`). Re-push конфига = команда `resync` на root.

Ключевое везение: оба стека уже используют Dapr pub/sub-компонент с одинаковым именем
`orderpubsub`. При общей docker-сети и общем Redis работают и pub/sub, и (через mDNS name
resolution Dapr) service invocation между стеками по APP_ID — без переименований.

---

## Архитектура интеграционного слоя

```
            typelib SquareIntegrationClient (gRPC+Dapr, за интерфейсом ISquareIntegration)
  VmsCfg ──register root(APP_ID)/push objects+props/report action status──►  Square (integration API)
 (ta_vms) ◄──ExecuteAction(uid)/resync  (Dapr service invocation на app-id vmscfg)──
```

### 1) Square — хост интеграции (расширяемый API, протокол Square)
Дорабатываем сырой протокол Square до устоявшегося вида, реализуя на его **gRPC + Dapr** API
(`Square/Protos/integro.proto`, `actions.proto`; consumer-склейка в
`Square/IntegrationUtilsLib/IntegrationSyncFull.cs`, рендер — `tree_alarm.client/src/actions/
ActionsControl.tsx`):
- приём root-объекта по APP_ID и его поддерева камер (`IntegroService.UpdateIntegro` +
  `IntegroTypesProto`/`InitTypes`: типы `vms` → `camera`);
- приём read-only свойств (`ProtoObjExtraProperty` → `UpdateProperties`);
- реестр доступных команд на объект и их вызов (`ActionsService.GetAvailableActions`/
  `ExecuteActions`), приём статусов (`UpdateActionResults`);
- **каскадное удаление** поддерева при удалении root; **per-object disable** в админ-режиме;
- **убрать** старый двунаправленный путь создания конфига из Square
  (`Topics.OnUpdateIntegros`/`OnDeleteIntegros`) — оставить за Square только презентацию + команды.

### 2) Typelib `SquareIntegrationClient` (реализовано, producer-сторона)
Тонкий клиент поверх Square-API, **gRPC + Dapr service invocation**, за интерфейсом
`ISquareIntegration` (SOLID + подменяемость):
- `UpsertObjects`/`PushFigures`/`PushProperties`/`PushStates`/`PushEvents`/`PushTracks`/`PushValues`/
  `RegisterIntegro`/`RegisterIntegroTypes`/`ReportActionStatus`/`GetObjects`/`GetProperties`/
  `GetIntegroByType`/`GetIntegroByIds`/`GenerateObjectId` — producer→Square и обратное чтение.
- Входящая сторона: `IObjectActions`/`ActionsServiceImpl` — хостинг обработчика
  `GetAvailableActions`/`ExecuteActions`/`ExecuteActionGetResult`/`CancelActions`, вызываемого Square
  по Dapr service invocation на APP_ID producer'а.
- **No-op реализация** `ISquareIntegration` для автономного режима (sink не задан) — гарантирует, что
  без интеграции producer работает как standalone.
- **Размещение: отдельный репозиторий `github.com/treealarm/SquareIntegrationClient`, подключаемый
  как git submodule** — `ta_vms/web_vms/SquareIntegrationClient` и `Square/SquareIntegrationClient`
  (по образцу `KeycloakAdmin`, который уже подключён как submodule в обоих репозиториях). Содержит
  `.proto`-контракт (`integro`/`tracks`/`actions`/`common`/`diagrams`) + сгенерированные gRPC-стабы —
  единый источник правды вместо вручную синхронизируемых копий. Используется **всеми** продьюсерами
  протокола: внешним (`ta_vms/VmsCfg`) и внутренними Square-продьюсерами (`AASubService`,
  `GrpcTracksClient`). Host-side (`Square/IntegrationHost`, `Square/IntegrationServerLib`,
  `Square/IntegrationUtilsLib/IntegrationSync*.cs`, завязанные на `Square/Domain`) — не переезжает,
  ссылается на submodule только за proto-типами.

### 3) VmsCfg — первый producer
`web_vms/VmsCfg` (`Program.cs`, `Grpc/CfgServiceImpl.cs`) подключает typelib и, **если интеграция
включена env-флагом** (напр. `SQUARE_INTEGRATION_SINK_APP_ID`):
- на старте: `RegisterRoot("vmscfg")` + полный push дерева камер и свойств из своего DbContext;
- на своих событиях `camera-config-changed`/`camera-state-changed`/`media-server-config-changed`
  (уже публикуются в `CfgServiceImpl`) — push дельт;
- объявляет команды: камера — `start/stop_recording`, `arm/disarm`, (позже) PTZ; root — `resync`;
- реализует обработчики команд через существующую внутреннюю логику cfg / вызовы других сервисов
  ta_vms (`CameraControlService.SetCameraState` `proto/vms_control.proto`, `web_vms`
  `CameraStateController`, PTZ `proto/onvif.proto`).
- **Автономность:** флаг не задан → typelib = no-op, VmsCfg ведёт себя ровно как сейчас. Соответствует
  правилу CLAUDE.md «no silent fallback»: интеграция — явная опциональная фича, документируется.

### Свойства и команды (самоописание)
- **Свойства камеры (RO):** `online`, `recording`, `armed`, `profile_name`, `last_alarm_ts`,
  `live_url`, `has_archive`, `disk_usage`. Square рендерит как состояния, не интерпретируя.
- **Команды:** только переключение состояния (запись/арм/PTZ) + `resync`. Существенный конфиг
  командами не меняется.
- **Граница владения:** функциональный конфиг — у ta_vms (в Square RO); позиция/имя/группировка — у
  Square.

---

## Phase 0 — Плумбинг: общая сеть + общий Redis + кросс-стек Dapr (выполнено)

> Реализовано иначе, чем планировалось изначально (оверлей + альтернативные Dapr-компоненты) —
> ниже описана фактическая, более простая схема. **Важно:** это означает отказ от автономности
> "из коробки" — `docker compose up` в ta_vms один, без поднятого Square/multitenant_admin,
> больше не работает (явное, осознанное решение пользователя).

Никакого отдельного оверлей-файла и альтернативных Dapr-компонентов не потребовалось:
- `ta_vms/docker-compose.yml` переименовывает свою неявную сеть `default` в `common_app_network`
  (`networks: default: name: common_app_network`, с `external: true`). Все сервисы ta_vms
  оказываются в одной сети с Square/multitenant_admin без единой правки на уровне отдельных
  сервисов. `external: true` здесь обязателен: свой ключ сети у ta_vms — `default`, у Square/
  multitenant_admin — `shared_network`, а compose пишет ключ в метку
  `com.docker.compose.network` и отказывается адоптировать сеть с чужим значением метки
  («network common_app_network was found but has incorrect label …»). Следствие: сеть создаёт
  Square/multitenant_admin, их стек должен стартовать первым.
- Собственный `redis`-сервис у ta_vms убран целиком — Dapr pub/sub и state store ta_vms
  (`init_files/dapr_components/docker/{orderpubsub,statestore}.yaml`, `redisHost: redis:6379`)
  используют тот же `redis`, что и multitenant_admin. **Причина, не просто стиль:** проверено
  эмпирически — если в одной сети два разных контейнера оба регистрируют alias `redis`, Docker
  отвечает на DNS-запросы случайным round-robin между ними; держать свой `redis` параллельно с
  чужим под тем же именем в одной сети нельзя, это давало бы постоянную, непредсказуемую потерю
  части сообщений pub/sub.
- `dapr-placement` остаётся раздельным (нужен только акторам, service invocation идёт через mDNS
  name-resolution в общей сети, не через placement).

`vmscfg`/VmsCfg обращается к Square исключительно через Dapr **service invocation** (gRPC-вызов на
`integrationhost`-app-id) — pub/sub между стеками не используется, общий Redis нужен только потому,
что ta_vms теперь физически не имеет своего.

## Phase 1 — Typelib + Square-API + VmsCfg-producer
1. Довести Square integration API (раздел «1) Square — хост»): протоколы/consumer/каскад/disable;
   убрать старый путь создания конфига из Square.
2. Реализовать typelib `SquareIntegrationClient` + `ISquareIntegration` (+ no-op).
3. Подключить typelib в `VmsCfg`: register root по APP_ID, push дерева/свойств, обработчики команд,
   гейт по env.

Файлы (реализовано для протокольного слоя — см. п.2 выше): `github.com/treealarm/
SquareIntegrationClient` (новый репозиторий, submodule в `ta_vms/web_vms/SquareIntegrationClient`
и `Square/SquareIntegrationClient`); `Square/IntegrationUtilsLib/{SquareIntegration,Utils,
IntegrationSync,IntegrationSyncFull}.cs` (host-side, трим), `Square/GrpcDaprLib.csproj` (убраны
`integro`/`tracks`/`actions` из компиляции), `Square/{AASubService,GrpcTracksClient,ValhallaLib,
IntegrationServerLib,LeafletAlarms}` (ссылки на submodule); `ta_vms/web_vms/VmsCfg/Program.cs`+
`Grpc/{CfgServiceImpl,SquareCameraPush,SquareAlarmSubscriber,SquareResyncService}.cs`. Ещё не
сделано: команды (`ActionsServiceImpl`-хостинг в VmsCfg), env в `.env`/`dockerhub/.env`,
launch.json'ы (см. CLAUDE.md ta_vms про обязательные места для новых env/сервисов).

## Phase 2 — Видео в UI Square
ta_vms уже отдаёт WebRTC live + HLS-архив через `web_vms` — в ta_vms писать ничего не нужно.
- В `Square/tree_alarm.client/src/` плеер: по свойству `live_url`/`vms_camera_id` строит URL к
  `web_vms`. Базовый URL — через конфиг фронта.
- Доступ из браузера: `web_vms` за reverse-proxy на том же origin, что Square (или CORS).
- Клик по камере → live; из тревоги/таймлайна → HLS-архив на нужный `ts`.

## Phase 3 — Единый вход (Keycloak) на краю
JWT-валидацию ставим **перед** `web_vms` (reverse-proxy/gateway: nginx+oauth2-proxy или шлюз Square),
проверяющий Keycloak-токены Square. Внутри сети `web_vms` без авторизации, снаружи напрямую
недоступен. Референс: `Square/LeafletAlarms/Authentication/ConfigureAuthentificationServiceExtensions.cs`.

> **Реализовано (частично, заранее):** логин + роли `admin`/`user` уже сделаны — но валидация JWT
> стоит **внутри** `web_vms.Server` (как и в самом `LeafletAlarms`, а не отдельный gateway),
> напрямую через `JwtBearerOptions.Authority`/OIDC discovery, без отдельного reverse-proxy.
> Использует **тот же realm `myrealm`**, что и Square (общий вход, отдельных пользователей под
> ta_vms не завели). Включается опционально через `KEYCLOAK_URL` (пусто → standalone, без
> авторизации, как раньше) — см. `web_vms/web_vms.Server/Authentication/`,
> `Controllers/AuthController.cs`. Сетевая связность — через общую `common_app_network` (Phase 0,
> теперь часть базового `docker-compose.yml`, без отдельного оверлея).
> Остаётся на будущее: typelib-интеграция (Phase 1/2) и, если понадобится, вынос валидации на
> отдельный gateway.

---

## Сводка изменений
- **ta_vms:** общая сеть + общий Redis вместо своего (Phase 0); typelib `SquareIntegrationClient`
  + правки в `VmsCfg`/`VmsFs` (гейт по env `SQUARE_INTEGRATION_SINK_APP_ID`) (Phase 1, частично);
  ничего в тяжёлом ядре.
- **Square:** read-only защита панели свойств для интеграционных объектов, REST-обёртка над
  `GetListByType`, новая вкладка "Monitor" (Phase 2, частично — см. ниже).
- **Общий:** новый typelib `SquareIntegrationClient` (`ISquareIntegration` + no-op).

## Проверка (по этапам)
- **Phase 0:** `multitenant_admin` создаёт `common_app_network`; ta_vms подключается тем же
  именем сети (без оверлея, `external: true`) и без своего Redis — проверить резолв app-id
  между стеками (Dapr service invocation `vmscfg` → `integrationhost`) и что Dapr pub/sub ta_vms
  (`orderpubsub`/`statestore`) подключается к общему `redis` от `multitenant_admin`.
  **ta_vms больше не запускается без поднятого Square/multitenant_admin** — автономность
  принесена в жертву единой сети по решению пользователя (см. CLAUDE.md).
- **Phase 1:** включить интеграцию env → в дереве Square появился root `vmscfg` и камеры; изменить
  конфиг камеры в ta_vms → дельта прилетела; команда `start_recording` из Square → запись включилась
  (статус по uid вернулся); `resync` → полный re-push; удалить root в Square → дети исчезли,
  интеграция разорвана; per-object disable работает.
- **Phase 2:** в UI Square live (WebRTC) и архив (HLS) играют через `web_vms`.
- **Phase 3:** прямой запрос к `web_vms` снаружи без токена отклонён; с валидным Keycloak-токеном —
  проходит.

## Открытые вопросы для этапа реализации
1. **Кросс-стек Dapr service invocation:** подтвердить, что при общей сети дефолтный mDNS-резолвер
   Dapr находит app-id обоих стеков (иначе настроить общий name-resolution компонент). Pub/sub через
   общий Redis работает независимо.
2. Что публикует `media_server` (C++) через `dapr_publisher.publish_json` — топики тревог/клипов?
   (`grep -rn publish_json media_server`) — нужны ли они typelib'у для свойства `last_alarm_ts`.
3. ~~typelib: чистые интерфейсы (SOLID, тестируемость, no-op для standalone); набор методов
   `ISquareIntegration`; размещение (submodule vs NuGet)~~ — сделано (`SquareIntegrationClient`
   submodule). Остаётся: схема самоописывающегося свойства/дескриптора команды для VmsCfg.
4. Финальный reverse-proxy для Phase 2–3 (nginx+oauth2-proxy vs существующий шлюз Square).
5. Точные имена env-флагов интеграции (напр. `SQUARE_INTEGRATION_SINK_APP_ID`) + добавить во все
   обязательные места (`.env`, `../ta_install/.env`, launch.json'ы) per CLAUDE.md.
