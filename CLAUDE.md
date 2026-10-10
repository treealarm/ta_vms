# Architecture rules

## Language conventions

Chat communication with the user is in Russian. Code — comments, identifiers, commit messages, log messages, docs — is always in English.

Everything addressed to the user rather than to the codebase follows the chat, not the code: **implementation plans (including plan files) and code-review findings are written in Russian.** The code they talk about stays English, so quoted identifiers, file paths and code snippets are of course unchanged.

## Engineering principles (mandatory)

For all new code and refactors, treat these as hard constraints rather than style preferences:

- **SOLID:** keep single responsibility boundaries clear, design against interfaces/abstractions, and avoid coupling unrelated concerns in one class/module.
- **KISS:** prefer the simplest design that meets the requirement; avoid speculative abstractions and unnecessary indirection.
- **YAGNI:** do not implement features, extension points, or configuration knobs before there is a concrete requirement.
- **DRY:** avoid duplicated business logic and duplicated protocol/format conversions; extract shared helpers when two paths must stay behaviorally identical.

`media_server` is built with **C++20**. Prefer modern C++20 techniques and standard library facilities over legacy patterns whenever the result is clearer and safer (`std::span`, ranges, RAII, `std::optional`, `std::variant`, structured bindings, constexpr helpers, etc.). Do not introduce pre-C++20 workarounds where a direct C++20 solution is available.

## MediaMTX API access

All interaction with the MediaMTX REST API goes through **VmsUtils** (`vmsutils` Dapr app-id). No other service (media_server, VmsCfg, web_vms, etc.) calls the MediaMTX API directly.

- media_server sends `camera_id + stream_name` to VmsUtils via gRPC
- VmsUtils resolves the assigned MediaMTX server (with its `api_url`) from VmsCfg, then queries the MediaMTX REST API
- Other services consume the results from VmsUtils — they do not hold or forward MediaMTX API URLs

## Deployment lives in ../ta_install

Production/standalone deployment is a **separate repository** (`../ta_install`, github
`treealarm/ta_install`): the product compose (prebuilt `treealarm/*` images, own broker, no
Square), its `.env`, `init_files/` and the image build/push scripts. (The analytics models are
not fetched at deploy time: `video_a` bakes them into its image, and its CI takes them from
`treealarm/analytics-models`, published by `video_a/scripts/push-models-image.sh`.) This repo's
root `docker-compose.yml` is **dev only** (build-from-source, joins the shared Square network).
Never re-grow a prod compose inside ta_vms.

## ta-deps is shared with video_a

`ta-deps` (this repo's `ta-deps/Dockerfile`) is the C++ toolchain + vcpkg base image for **two**
consumers: this repo's `media_server` (`vms` service) and the separate `video_a` repo's
`analytics-worker`, which builds `FROM ta-deps` via a sibling checkout (see `video_a/scripts/build.sh`).
It installs protobuf/grpc/spdlog/ffmpeg for media_server plus openvino for video_a — when changing
its package list, consider both consumers.

`video_a`'s CI does not follow this repo: it builds against the `treealarm/ta-deps` tag written in
`video_a/ta-deps.tag`. After publishing a new `ta-deps` (`../ta_install/scripts/push-deps-images.sh`),
bump that file in `video_a`, in a commit of its own — until then video_a keeps building on the old
base, which is safe but means a package added here for video_a is not there yet.

**Adding an ffmpeg feature to the shared vcpkg tree breaks every stale build directory.**
`media_server` and `video_a` configure against `/mnt/wdblue/vcpkg` in classic mode, and both
link ffmpeg through `pkg_check_modules(FFMPEG ...)` — as does `roi_transcode`, from its own
repository, against the same tree. That call caches its answer in `CMakeCache.txt` and never
asks pkg-config again. So when ffmpeg was once rebuilt there with `qsv,vaapi`,
`libavutil.a` gained
`hwcontext_vaapi.o` while every build directory configured before that kept a link line with
no `-lva`. The result is a wall of `undefined reference to vaGetDisplayDRM` that looks like a
source problem and is not: the `.pc` file is already correct, the cache is not. Delete the
build directory and reconfigure. Docker builds never hit this — they configure from scratch.

**`roi_transcode` does not use it.** It lives in its own repository now (github
`treealarm/roi_transcode`, checked out beside this one) and has its own base, `roi-deps`,
built there by `./docker/build.sh`. The split is load-bearing for `qsv` — the oneVPL
dispatcher is dead weight without `libmfx-gen1.2` in a runtime image — and for openvino,
which is 143 of the 234 minutes ta-deps takes to build and which roi_transcode does not link.

**`vaapi` is the exception, and it is in `ta-deps` deliberately.** media_server transcodes live
HEVC to H.264 for every WebRTC viewer, one encode per stream, continuously; that is the largest
sustained CPU cost in the service, and `h264_vaapi` measured 43% less CPU per stream on an
i7-12700 iGPU. It brings the system `libdrm-dev` vcpkg's libva demands into `ta-deps` with it.
`video_a`'s analytics-worker pays for this in image size and nothing else.

Building it is not using it. Three things have to line up in the *runtime* image, and each one
failing looks the same from outside — a service that merely burns CPU: an iHD driver new enough
to encode (hence ubuntu:24.04 in the root `Dockerfile`'s runtime stage, not 22.04), vcpkg's own
`libva.so.2` shipped beside the binary (the distribution's older one wins otherwise and the first
hardware encode dies on `undefined symbol: vaMapBuffer2`, lazily, hours after startup), and
`LIBVA_DRIVERS_PATH` pointing at the distribution's `dri` directory (vcpkg's libva searches its
own build prefix, which does not exist in the image). `/dev/dri` passthrough is opt-in per host —
see `../ta_install/docker-compose.gpu.yml.example`.

Both images use an overlay vcpkg triplet
(`<image>/triplets/x64-linux.cmake`) to build release-only (no debug artifacts — vcpkg's
`VCPKG_BUILD_TYPE` env var is a no-op, it only works from inside a triplet file); consumers must
still pass `-DCMAKE_BUILD_TYPE=Release` on their own `cmake` configure step to select the release
libs.

## Граница между `roitrc/` и `roitrc/ta_roienc/`

`roitrc/ta_roienc` — submodule на github `treealarm/roi_transcode`, библиотека `ta_roienc`
(адаптивное ROI-кодирование). В репозитории только код библиотеки и её C++ юнит-тесты; она
сознательно не знает ни про gRPC, ни про Dapr, ни про protobuf. Править её можно — это наша
библиотека, но всё, что имеет смысл только для ta_vms, живёт здесь, а не там.

**Потребителей у неё два, и второй меняет цену ошибки.** Кроме `roitrc` её линкует
`media_server` — живой транскод HEVC→H.264 для WebRTC идёт через `ta_roienc::Session`, потому
что в libavcodec нет авто-подбора железа и `avcodec_find_encoder` всегда отдаёт libx264
(см. `media_server/writer/processors/hw_transcode_processor.cpp`).
**Бэкенд по умолчанию — `Backend::Vaapi`, а не `Backend::Auto`**, и разница здесь смысловая:
библиотека линкуется только ради железа. Если аппаратный бэкенд не открылся — или цепочка
`auto`, заданная явно через `VMS_VIDEO_ENCODER`, свелась к программному, — поток отдаётся
обратно `compat_sink`, и его берёт `video_transcode_processor`. У того есть `tune=zerolatency`,
а у программного бэкенда библиотеки его нет: 34 кадра в конвейере против 4 через
`h264_vaapi`, то есть полторы секунды задержки на живом видео.
Сломанный bump submodule'а гасит живое видео, а не только очередь клипов. Обновляй версию
отдельным коммитом и проверяй оба пути.

`roitrc/` — всё, что про пережатие принадлежит нам: gRPC-контракт, который зовёт `VmsFs`,
модель заданий (повторяет EdgeImport из `media_server`), Dapr-уведомления и песочница путей
`ROI_ALLOWED_ROOTS`. Стрелка зависимости одна и узкая: линкуется только публичный API —
`ta_roienc/ta_roienc.h`, `ta_roienc/session.h`, `ta_roienc/types.h`, — и во внутренности
библиотеки не лезет ничего. Если новому коду понадобился `src/` энкодера, это признак, что он
не на той стороне границы.

Контракт `proto/roi_transcode.proto` живёт здесь, рядом с остальными, в единственном
экземпляре.

Собирается это `roitrc/CMakeLists.txt` через `add_subdirectory(ta_roienc)` с выключенными
`TA_ROIENC_BUILD_TESTS` и `TA_ROIENC_INSTALL`: юнит-тесты библиотеки гоняются в её
репозитории. Свои проверки сервиса — `roitrc/scripts/check.sh` (старт при битой конфигурации,
оба входа gRPC, песочница). Базовый образ `roi-deps` строится из `docker/intel/` submodule'а;
`roitrc/Dockerfile` берёт контекстом корень репозитория, потому что ему нужны и `roitrc/`, и
`proto/`.

Обновление версии библиотеки — обычный submodule bump, отдельным коммитом, чтобы было видно
в истории, какой энкодер поехал в релиз.

## Adding a new service

When adding a new service (C# or C++), you **must** update all of the following:
1. `.env` — add the service's port and any Dapr app-id env vars
2. `../ta_install`: `docker-compose.yml` (image-based service + dapr sidecar), `.env`, and the image pair in `scripts/push-images.sh`
3. `docker-compose.yml` — add service + dapr sidecar entries (with `profiles: [app]`)
4. `.vscode/launch.json` — add a debug launch configuration for local development
5. `.vscode/tasks.json` — add `prepare:<name>`, `dapr:<name>:start`, `dapr:<name>:stop` tasks (copy pattern from vmslogger)
6. `web_vms/web_vms.sln` — add the project (for C# services)

Omitting any of these causes a startup crash or missing debug config in dev.

## Environment variables

When adding a new required environment variable to any service:
1. Add it to `.env` (docker-compose local dev values).
2. Add it to `../ta_install/.env` (production/installation values).
3. Add it to **every** `launch.json` that launches that service — both the service-specific one (`web_vms/.vscode/launch.json`, `media_server/.vscode/launch.json`) and the root `.vscode/launch.json`. Missing it in any one of these causes a startup crash in dev.

**No silent fallbacks.** If an environment variable is required, the service must throw (or return non-zero) immediately on startup when it is missing — never silently default to a hardcoded value. A missing required variable is a misconfiguration and must be visible immediately, not discovered at runtime through unexpected behavior.

- C# pattern: `Environment.GetEnvironmentVariable("X") ?? throw new InvalidOperationException("Required environment variable 'X' is not set")`
- C++ pattern: use `require_env("X")` (which throws `std::runtime_error`)
- Exception: genuinely optional variables that enable/disable a feature (e.g. `APP_ID` in VmsFs — omitting it disables multi-instance scoping). Document the default behavior in a comment when you use an optional variable.

## Inter-service communication: config changes via pub/sub

When a service needs to react to a configuration change that originated in another service, use the **pub/sub pattern** — do not poll or call the source service directly on every request.

**Rule:** VmsCfg is the single source of truth for all configuration (cameras, storages, recording profiles, MediaMTX servers, etc.). Any other service that caches or acts on this configuration must subscribe to change events published by VmsCfg, then fetch the updated data itself.

**Pattern:**
1. VmsCfg publishes an event to a topic (e.g. `server-config-changed`, `storage-config-changed`) after a write operation succeeds.
2. The subscriber receives the event and invalidates its local cache or re-fetches the relevant slice of config from VmsCfg via gRPC.
3. The subscriber never assumes the event payload contains the full new state — it only uses the event as a signal to pull fresh data.

**Existing examples in this codebase:**
- `CfgServiceImpl.InvalidateFsCache` publishes `server-config-changed` → `VmsFs.CameraConfigSubscriber` re-fetches camera config.
- `StorageServiceImpl` publishes `storage-config-changed` → `VmsFs.CameraStorageSubscriber` re-fetches storage assignments.

### Config-change events are addressed to a node, not to a camera

Camera configuration changes travel on a single topic, `server-config-changed`, with the payload
`{"server_id": "...", "camera_ids": [...]}`. One event per affected server, not per camera.

The event is delivered to every subscriber — each service has its own Dapr app id and therefore its
own subscription — so `server_id` is what lets a node-scoped subscriber (`media_server`, `VmsOnvif`)
drop what is not its business *before* doing any work. Deciding that by fetching the camera from
VmsCfg would cost one round trip on every instance in the deployment, all but one of which throws
the answer away.

`camera_ids` is **addressing, not state**: it narrows which cache entries to drop, and nothing more.
The subscriber still fetches the current configuration from VmsCfg — an event that arrives late or
out of order must not be able to install a stale value. An empty list means "something node-wide
changed" (a MediaMTX row, a pairing) and asks for a full reconcile.

Do not add a per-server or per-camera suffix to the topic name. A subscriber that needs cameras
outside its own node — `VmsSquareSingleton` does — would then have to know every server id in the
deployment just to subscribe.

**Do not:**
- Add a direct gRPC call from service A to VmsCfg inside a hot path (e.g. per-request, per-packet) to read config that rarely changes — cache it and invalidate via pub/sub instead.
- Embed config values in event payloads as the authoritative state — events are notifications, not data carriers.

### Exception: services that may run in more than one replica

Pub/sub invalidation only works for a **single-instance** consumer. All replicas of a service share
one Dapr app id, and the app id is the `consumerID` — so on RabbitMQ they all consume the same
queue, and each event is delivered to exactly one replica. The others never learn and keep serving
stale data until their TTL runs out. That is a work queue, and cache invalidation needs a broadcast.
(This was equally true of the Redis Streams consumer group the bus used before, and is not a
property of the broker: it is what one app id means.)

For a service that can be scaled horizontally (today: `web_vms.Server`), use a **version key in the
shared state store** instead:
1. VmsCfg writes a new value to a well-known key after the write succeeds (`cam-perm-version`, see
   `CfgServiceImpl.BumpCameraPermissionsVersionAsync`) — the value is meaningless, only "different
   from last time" matters.
2. The consumer re-reads that key at most once per short interval and drops its cache when it moved
   (`CameraAccessService.EnsureFreshAsync`), keeping its own TTL as the backstop for a state store
   outage.

Do not "fix" this with a per-instance `consumerID`: it works, but leaves an orphaned queue behind on
every restart — one per instance that ever started, each still bound to the topic exchange and each
accumulating messages nobody reads.

### Scaling model per service

- `web_vms.Server` — stateless, scales by **replicas**. Keep it that way: no per-instance session
  tables (WHEP session state travels in an AES-GCM sealed token, see `WhepSessionToken`), no
  subscriptions, no local files.
- `media_server` (`vms`) and `VmsFs` — scale by **sharding**: one pair per node, pinned to that
  node's storage. More capacity means another server↔FS pair, never a second replica of the same
  app id.
- `VmsOnvif` and `VmsOnvifActors` — also **sharded by node**, for a different reason: ONVIF reaches a
  camera over the same local subnet RTSP does, so an instance can only serve the cameras of the media
  server it sits next to. `OWN_SERVER_ID` names that node; the actor type name carries it too
  (`CameraActorType`), because Dapr placement routes actors by type name and would otherwise put a
  camera's actor on a host with no route to it.
- `VmsSingleton` — **exactly one instance, ever.** The suffix is the deployment contract: this is
  where work that must happen once for the whole system lives, together with in-memory aggregation a
  second copy would split rather than share (all replicas of one app id share a Dapr consumer group,
  so each would see half the events). Anything that does not need that property must not go here —
  otherwise it becomes a dumping ground and the guarantee stops meaning anything.
- `VmsAnalyticsSingleton` — also exactly one instance, but for its own reason: it is the control
  plane of analytics, not a worker. It decides which `analytics-worker` watches which camera and
  holds one long-lived `StreamDetections` per worker — and a stream, unlike a queue, is delivered to
  every subscriber in full, so a second instance would duplicate every detection: the row, the crop
  file and the `RetainEvent`. Its re-identification gallery, event grouping and debounce are shared
  state in the same way. **The scaling axis is the workers**, which are already sharded across
  `DAPR_ANALYTICS_WORKER_APP_IDS` by rendezvous hash. Sharding this service by camera is possible but
  costs cross-camera re-identification between shards — decide that deliberately, not by adding a
  replica.
- `VmsCfg`, `VmsUtils`, `VmsLogger` — `VmsCfg` and `VmsUtils` are replicable (their once-only work
  lives in `VmsSingleton`); `VmsLogger` is sharded by node, because it reads node-local files.
- `roitrc` (the ROI re-compression service; the `ta_roienc` library it links lives in its own repository) — **sharded by node**, and this one is a correctness requirement
  rather than a capacity choice. It is handed absolute paths to clips on the node's own disk, so an
  instance elsewhere either finds nothing there or finds a *different* node's file at the same path
  and overwrites it. Which instance a node talks to therefore comes from that node's entry in
  `MEDIA_SERVER_PAIRS` (fifth part, optional), resolved through VmsCfg — never from a global env
  var, which cannot express "the one next to me". A node that names none simply does not
  re-compress.

**Naming rule:** a service that cannot be replicated carries `Singleton` in its project name and Dapr
app-id. A reader must be able to tell the scaling class from the name alone, without reading
`Program.cs` to discover a background job hiding inside.

## Debug "entity state" presence registry

For runtime debugging, any service can publish a periodic snapshot of an entity's in-memory
state (e.g. "camera X stream Y is currently recording into file Z") to the `debug-entity-state`
topic on the existing `orderpubsub` pub/sub component. This is a **best-effort, non-persistent**
mechanism — no delivery guarantees, no history, nothing is written to a database.

**Envelope** (raw JSON, one message per entity per tick):
```json
{ "service": "vms", "entity_type": "stream", "entity_id": "cam1/main", "state": { "...": "..." } }
```
- `service` — publisher's `APP_ID`.
- `entity_type` / `entity_id` — free-form identifiers chosen by the publisher.
- `state` — arbitrary JSON object, no fixed schema; each entity publishes whatever fields are
  relevant to it.

**VmsSingleton** owns the in-memory registry: it subscribes to `debug-entity-state`, keys entries by
`(service, entity_type, entity_id)`, and tracks `last_seen`. `stale` is computed on read
(`now - last_seen > 30s`); a periodic sweep removes entries not seen for `> 5min`. The registry is
exposed via the `ListEntityStates` gRPC (consumed by `web_vms.Server`'s `/api/entitystate`
endpoint and the "Entity State" debug tab in the frontend).

**Publishing:**
- C# services: call `builder.AddVmsDebugState()` (from `VmsLog`), then inject
  `EntityStatePublisher` and call `Publish(entityType, entityId, state)`.
- C++ services: use `entity_state_publisher` (in `media_server/grpc_layer`) which wraps
  `dapr_publisher::publish_json`.

## Database access

Use **Entity Framework Core** for all database queries. Do not write raw SQL via `NpgsqlDataSource` / `NpgsqlCommand` in new code. Use `IDbContextFactory<T>` (or the injected `DbContext`) and LINQ for queries.

Exceptions where raw SQL is acceptable: complex bulk operations or SQL constructs that EF cannot express (e.g. `INSERT … ON CONFLICT DO UPDATE`), but keep them minimal and isolated inside repository methods.

### Creating a database: name it in `.env`, nothing else

Databases are created by exactly one thing — `scripts/prepare-databases.sh`, run as the `db-init`
compose service — and it creates **every** database named by a `POSTGRES_*_DB` variable in the
environment. So a new service's database is added by putting `POSTGRES_<NAME>_DB` into `.env` (and
`../ta_install/.env`), and by nothing else.

Do **not** go back to `docker-entrypoint-initdb.d`: that hook only fires when the data directory is
initialised from empty, so anything added to it is silently skipped on every deployment that
already has data — which is all of them after the first. And do not hardcode a list of database
names anywhere: names are stamped per tenant (see `multitenant_admin/mt_admin/DbAdmin`), so a fixed
list is wrong by construction.

`DbMigrator` migrates the schema *inside* an existing database; it does not create one. A missing
database now fails fast with a message naming it, instead of retrying "Postgres not ready" forever.

## VmsOnvifActors — actors only

`VmsOnvifActors` hosts **only Dapr Actors** (currently `CameraActor` for PTZ). Do not add BackgroundServices, controllers, pub/sub subscribers, or any non-actor logic to this project. Business logic that needs ONVIF camera access but is not actor-based belongs in `VmsOnvif`.

## Docker image versions

In `docker-compose.yml` use `latest` for all third-party images. Do not pin specific version tags (e.g. `1.14.4`) — use `latest` instead.

## Protobuf — no `reserved` fields

When removing a field from a `.proto` message, delete it outright. Do not use `reserved` — field numbers are not reused anyway and `reserved` is just noise. Same applies to field name reservations.

## Frontend: a control belongs next to what it acts on

A button reads as belonging to whatever it sits beside. Put it there, and nowhere else.

- Acts on a **list** — refresh it, discover things to add to it, filter it: the list's own header,
  beside "+". `MasterDetailLayout` takes them in `listActions`. The header is narrow, so these are
  icon buttons with tooltips.
- Acts on the **selected item** — save, cancel, delete: `DetailToolbar`, above the item's form.
- Acts on the **page as a whole** — a mode switch that changes what everything on it means, like
  `AclModeSwitch`: `MasterDetailLayout`'s `footer`. This is the rare one; reach for it last.

A row of buttons parked at the bottom of the page is the failure mode this replaces: the reader has
to work out what each one applies to, and "Refresh" under a split view could plausibly mean either
pane. Distance from the target is what makes it ambiguous, so the fix is always to move the control,
never to rename it.

## Database columns — no speculative additions

Only add columns that are actively used. Do not add `created_at`, `updated_at`, audit fields, or any other column "just in case" — if it is not read or queried by existing code, it does not belong in the schema. Add it when there is a concrete requirement.

## Square integration

ta_vms встраивается в основную систему **Square** (`../Square`, мониторинг объектов на карте) как
видеоподсистема. Логин и базовый Phase 1/2 (камеры на карте, свойства, алармы, вкладка "Monitor" в
Square) реализованы; план — [`docs/square-integration-plan.md`](docs/square-integration-plan.md).
Любое дальнейшее проектирование и развитие видеоподсистемы **не должно противоречить** этому плану.
Соблюдай инварианты:

- **Общая с Square docker-сеть обязательна, но данных ta_vms в чужих контейнерах больше нет.**
  ta_vms **всегда** живёт в сети `common_app_network` (тот же `name:`, что у Square/
  `multitenant_admin`, но `external: true` — сеть создаёт Square/`multitenant_admin`, они должны
  подняться первыми; свой ключ сети у ta_vms `default`, у них `shared_network`, а compose
  отказывается подхватывать чужую сеть с несовпадающим ключом в метке
  `com.docker.compose.network`). Нужна она **только** для Dapr service invocation в Square и
  резолва app-id через mDNS.
  Всё состояние и вся шина — свои: **state store** это собственный PostgreSQL (база
  `POSTGRES_STATE_DB`, компонент `init_files/dapr_components/statestore.yaml`), **pub/sub** — свой
  `rabbitmq` (`init_files/dapr_components/orderpubsub.yaml`). Чужому `redis` не место ни там, ни
  там: он объявлен без volume, а при штамповке баз под тенанта общий Redis с `keyPrefix: "name"`
  давал бы всем тенантам один и тот же ключ `cam-perm-version`. Отдельно про Redis: два разных
  контейнера с одинаковым alias `redis` в одной сети заставляют Docker отвечать на DNS-запросы
  случайным round-robin между ними (проверено эмпирически) — поэтому свой Redis рядом с чужим
  держать было нельзя, и это одна из причин, по которым брокером стал RabbitMQ (вторая — под
  Windows Redis нативно не ставится, а в планах автономный инсталлятор).
  Функции, не связанные с интеграцией (login, push в Square), гейтятся по env
  (`KEYCLOAK_URL`, `SQUARE_INTEGRATION_SINK_APP_ID`) — сетевая связность безусловна, не за гейтом.
- **`VmsSquareSingleton` — единственная точка подключения интеграции** (producer-сторона). Тяжёлое
  ядро (C++ `media_server` и пр.) интеграцией не затрагивается. Не размазывай интеграционную логику
  по другим сервисам: `VmsCfg` про Square не знает вообще и лишь публикует свои обычные события
  (`server-config-changed`, `camera-state-changed`), на которые интегратор подписан.
- **Идентичность продьюсера в дереве Square — `SQUARE_INTEGRATION_PRODUCER_ID`, а не `APP_ID`
  процесса.** Из этого значения детерминированно выводится id корневого объекта, поэтому обе стороны
  задают его конфигурацией и ничем не обмениваются в рантайме. Значение постоянно: смена
  осиротит всё поддерево, уже зарегистрированное под старым корнем.
- **Конфиг — только снизу вверх.** Конфигурация камер принадлежит ta_vms и пушится в Square; наружу
  она read-only. Изменение состояния извне — **только командами** (actions: запись/арм/PTZ/`resync`),
  никогда не правкой конфига из Square. В Square для интеграционных объектов (с `i_name`) панель
  свойств — read-only, и для них не регистрируется иерархия типов (`UpdateIntegroTypes`), чтобы
  нельзя было создать их руками из дерева Square.
- **root-объект интеграции = `SQUARE_INTEGRATION_PRODUCER_ID`** (историческое значение — `vmscfg`,
  когда продьюсером была `VmsCfg`); команды — через Dapr service invocation, асинхронно
  (handle/uid сразу, статус позже). Обработчики команд ещё не подключены.
- **Не конфликтуй с базисом интеграции:** общая docker-сеть `common_app_network` и Dapr service
  invocation поверх неё (сообщениями по шине стороны не обмениваются: `VmsSquareSingleton` подписан
  только на собственные топики ta_vms, а в Square ходит вызовами — поэтому шина ta_vms своя,
  RabbitMQ, и имя компонента `orderpubsub` совпадает со Square лишь исторически), раздельные
  PostgreSQL и раздельные брокеры, модель
  самоописывающихся свойств/команд. Перед добавлением новых топиков, портов, env или сервисов
  сверяйся с планом, чтобы не создавать будущих коллизий.
