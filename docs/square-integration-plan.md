# Интеграция vms_rec в Square: typelib-клиент + VmsCfg как producer

> Статус: **план на будущее, не реализован.** Документ — ориентир для развития видеоподсистемы.
> Текущая разработка не должна противоречить этому плану (см. раздел «Square integration (future)»
> в `CLAUDE.md`). Саму интеграцию выполняем позже, отдельной задачей.

## Context

`/mnt/wdblue/tests/vms_rec` — распределённая VMS (запись/стриминг на базе MediaMTX).
Square (соседний проект `../Square`) — основная система мониторинга объектов на карте (Leaflet).
Родственные проекты одного вендора с почти идентичным стеком: **Dapr + Redis (pub/sub) +
PostgreSQL + .NET 8 + gRPC + React**. Нужно встроить vms_rec в Square так, чтобы vms_rec
**по-прежнему запускался полностью автономно**.

Согласованные решения:
- **Глубина:** поэтапно, в итоге всё (события/команды → видео в UI → единый вход).
- **Конфиг/камеры:** vms_rec — владелец функционального конфига; конфиг **пушится снизу вверх**
  (vms_rec → Square), в Square — read-only. Square владеет «презентацией» (позиция/имя/группировка).
  Менять состояние из Square — **только командами**.
- **Инфраструктура:** общая шина Redis (pub/sub) + общая docker-сеть, **раздельные PostgreSQL**.
- **Форма слоя:** Square — хост интеграции с расширяемым **gRPC + Dapr** API (+ REST). Producer —
  переиспользуемый **typelib-клиент** (аналог `IntegrationUtilsLib`, но со стороны отдающего),
  который подключает `VmsCfg`. Протокол — **напрямую протокол Square** (не нейтральный).
- **Связанность:** Square свободно перерабатываем; vms_rec — трогаем **только `VmsCfg`** (подключение
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
 (vms_rec) ◄──ExecuteAction(uid)/resync  (Dapr service invocation на app-id vmscfg)──
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

### 2) Typelib `SquareIntegrationClient` (новый, producer-сторона)
Тонкий клиент поверх Square-API, **gRPC + Dapr service invocation**, за интерфейсом
`ISquareIntegration` (SOLID + подменяемость):
- `RegisterRoot(appId, name)` / `UpsertObjects(tree)` / `UpdateProperties(...)` /
  `DeclareActions(objectId, descriptors)` / `ReportActionStatus(uid, progress, result)` — producer→Square.
- Входящая сторона: хелпер для хостинга обработчика `ExecuteAction(objectId, name, params, uid)` и
  `resync`, вызываемого Square по Dapr service invocation на APP_ID producer'а (асинхронно: сразу
  ack+uid, статус позже через `ReportActionStatus`).
- **No-op реализация** `ISquareIntegration` для автономного режима (sink не задан) — гарантирует, что
  без интеграции producer работает как standalone.
- Размещение: общий проект/NuGet, переиспользуемый и Square, и vms_rec (тащит протоколы Square).

### 3) VmsCfg — первый producer
`web_vms/VmsCfg` (`Program.cs`, `Grpc/CfgServiceImpl.cs`) подключает typelib и, **если интеграция
включена env-флагом** (напр. `SQUARE_INTEGRATION_SINK_APP_ID`):
- на старте: `RegisterRoot("vmscfg")` + полный push дерева камер и свойств из своего DbContext;
- на своих событиях `camera-config-changed`/`camera-state-changed`/`media-server-config-changed`
  (уже публикуются в `CfgServiceImpl`) — push дельт;
- объявляет команды: камера — `start/stop_recording`, `arm/disarm`, (позже) PTZ; root — `resync`;
- реализует обработчики команд через существующую внутреннюю логику cfg / вызовы других сервисов
  vms_rec (`CameraControlService.SetCameraState` `proto/vms_control.proto`, `web_vms`
  `CameraStateController`, PTZ `proto/onvif.proto`).
- **Автономность:** флаг не задан → typelib = no-op, VmsCfg ведёт себя ровно как сейчас. Соответствует
  правилу CLAUDE.md «no silent fallback»: интеграция — явная опциональная фича, документируется.

### Свойства и команды (самоописание)
- **Свойства камеры (RO):** `online`, `recording`, `armed`, `profile_name`, `last_alarm_ts`,
  `live_url`, `has_archive`, `disk_usage`. Square рендерит как состояния, не интерпретируя.
- **Команды:** только переключение состояния (запись/арм/PTZ) + `resync`. Существенный конфиг
  командами не меняется.
- **Граница владения:** функциональный конфиг — у vms_rec (в Square RO); позиция/имя/группировка — у
  Square.

---

## Phase 0 — Плумбинг: общая сеть + общий Redis + кросс-стек Dapr

Цель: оба стека рядом, делят шину Redis и видят app-id друг друга для service invocation.
vms_rec — только оверлей и альтернативные Dapr-компоненты (конфиг, не ядро).

Создать **`vms_rec/docker-compose.integration.yml`** (оверлей поверх базового):
- внешняя сеть Square `common_app_network` (`external: true`), подключить к ней сервисы, которым
  нужна кросс-стек связь (минимум `vmscfg` + его сайдкар, `web_vms`);
- Dapr-сайдкары vms_rec → общий Redis Square: смонтировать альтернативную папку
  `vms_rec/init_files/dapr_components_integration/` (`orderpubsub.yaml`/`statestore.yaml` с
  `redisHost: redis:6379`); базовые файлы (`localhost:6379`) остаются для автономного режима;
- развести host-порты: `mediamtx` публикует `8000`, Square HTTP UI тоже `8000` — убрать/переназначить
  у mediamtx;
- раздельные PostgreSQL и `dapr-placement` (placement нужен только акторам; service invocation идёт
  через mDNS name-resolution в общей сети, не через placement).

Базовый `vms_rec/docker-compose.yml` не меняется → `docker compose up` = автономный стек.

## Phase 1 — Typelib + Square-API + VmsCfg-producer
1. Довести Square integration API (раздел «1) Square — хост»): протоколы/consumer/каскад/disable;
   убрать старый путь создания конфига из Square.
2. Реализовать typelib `SquareIntegrationClient` + `ISquareIntegration` (+ no-op).
3. Подключить typelib в `VmsCfg`: register root по APP_ID, push дерева/свойств, обработчики команд,
   гейт по env.

Файлы: `Square/Protos/*`, `Square/IntegrationUtilsLib/*`, `Square/Domain/PubSubTopics/Topics.cs`,
`Square/tree_alarm.client/src/*`; НОВЫЙ общий проект `SquareIntegrationClient` (+ в обоих .sln);
`vms_rec/web_vms/VmsCfg/Program.cs`+`Grpc/CfgServiceImpl.cs`; env в `.env`/`dockerhub/.env` обоих,
launch.json'ы (см. CLAUDE.md vms_rec про обязательные места для новых env/сервисов).

## Phase 2 — Видео в UI Square
vms_rec уже отдаёт WebRTC live + HLS-архив через `web_vms` — в vms_rec писать ничего не нужно.
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
> vms_rec не завели). Включается опционально через `KEYCLOAK_URL` (пусто → standalone, без
> авторизации, как раньше) — см. `web_vms/web_vms.Server/Authentication/`,
> `Controllers/AuthController.cs`, `docker-compose.integration.yml` (Phase 0 connectivity).
> Остаётся на будущее: typelib-интеграция (Phase 1/2) и, если понадобится, вынос валидации на
> отдельный gateway.

---

## Сводка изменений
- **vms_rec:** оверлей+альт. Dapr-компоненты (Phase 0); правки **только в `VmsCfg`** (подключение
  typelib, гейт по env) (Phase 1); ничего в тяжёлом ядре.
- **Square (свободно):** доведённый integration API + consumer/каскад/disable; видео-плеер; proxy.
- **Общий:** новый typelib `SquareIntegrationClient` (`ISquareIntegration` + no-op).

## Проверка (по этапам)
- **Автономность (гейт после каждого этапа):** `cd vms_rec && docker compose up` без оверлея и без
  env интеграции — стек как раньше (`web_vms:5134`, live/архив, VmsCfg обычный).
- **Phase 0:** поднять Square (создаётся `common_app_network`), затем vms_rec с оверлеем; проверить
  подключение сайдкаров к общему redis и резолв app-id между стеками (Dapr-инвок `vmscfg` ↔
  `leafletalarms`), отсутствие конфликта порта 8000.
- **Phase 1:** включить интеграцию env → в дереве Square появился root `vmscfg` и камеры; изменить
  конфиг камеры в vms_rec → дельта прилетела; команда `start_recording` из Square → запись включилась
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
3. typelib: чистые интерфейсы (SOLID, тестируемость, no-op для standalone) — принято; финализировать
   набор методов `ISquareIntegration` и схему самоописывающегося свойства/дескриптора команды.
4. Финальный reverse-proxy для Phase 2–3 (nginx+oauth2-proxy vs существующий шлюз Square).
5. Точные имена env-флагов интеграции (напр. `SQUARE_INTEGRATION_SINK_APP_ID`) + добавить во все
   обязательные места (`.env`, `dockerhub/.env`, launch.json'ы) per CLAUDE.md.
