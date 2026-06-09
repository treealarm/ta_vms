# Architecture rules

## MediaMTX API access

All interaction with the MediaMTX REST API goes through **VmsUtils** (`vmsutils` Dapr app-id). No other service (media_server, VmsCfg, web_vms, etc.) calls the MediaMTX API directly.

- media_server sends `camera_id + stream_name` to VmsUtils via gRPC
- VmsUtils resolves the assigned MediaMTX server (with its `api_url`) from VmsCfg, then queries the MediaMTX REST API
- Other services consume the results from VmsUtils — they do not hold or forward MediaMTX API URLs

## Adding a new service

When adding a new service (C# or C++), you **must** update all of the following:
1. `.env` — add the service's port and any Dapr app-id env vars
2. `dockerhub/.env` — same as above
3. `docker-compose.yml` — add service + dapr sidecar entries (with `profiles: [app]`)
4. `.vscode/launch.json` — add a debug launch configuration for local development
5. `.vscode/tasks.json` — add `prepare:<name>`, `dapr:<name>:start`, `dapr:<name>:stop` tasks (copy pattern from vmslogger)
6. `web_vms/web_vms.sln` — add the project (for C# services)

Omitting any of these causes a startup crash or missing debug config in dev.

## Environment variables

When adding a new required environment variable to any service:
1. Add it to `.env` (docker-compose local dev values).
2. Add it to `dockerhub/.env` (production/hub values).
3. Add it to **every** `launch.json` that launches that service — both the service-specific one (`web_vms/.vscode/launch.json`, `media_server/.vscode/launch.json`) and the root `.vscode/launch.json`. Missing it in any one of these causes a startup crash in dev.

**No silent fallbacks.** If an environment variable is required, the service must throw (or return non-zero) immediately on startup when it is missing — never silently default to a hardcoded value. A missing required variable is a misconfiguration and must be visible immediately, not discovered at runtime through unexpected behavior.

- C# pattern: `Environment.GetEnvironmentVariable("X") ?? throw new InvalidOperationException("Required environment variable 'X' is not set")`
- C++ pattern: use `require_env("X")` (which throws `std::runtime_error`)
- Exception: genuinely optional variables that enable/disable a feature (e.g. `APP_ID` in VmsFs — omitting it disables multi-instance scoping). Document the default behavior in a comment when you use an optional variable.

## Inter-service communication: config changes via pub/sub

When a service needs to react to a configuration change that originated in another service, use the **pub/sub pattern** — do not poll or call the source service directly on every request.

**Rule:** VmsCfg is the single source of truth for all configuration (cameras, storages, recording profiles, MediaMTX servers, etc.). Any other service that caches or acts on this configuration must subscribe to change events published by VmsCfg, then fetch the updated data itself.

**Pattern:**
1. VmsCfg publishes an event to a topic (e.g. `camera-config-changed`, `storage-config-changed`) after a write operation succeeds.
2. The subscriber receives the event and invalidates its local cache or re-fetches the relevant slice of config from VmsCfg via gRPC.
3. The subscriber never assumes the event payload contains the full new state — it only uses the event as a signal to pull fresh data.

**Existing examples in this codebase:**
- `CfgServiceImpl.InvalidateFsCache` publishes `camera-config-changed` → `VmsFs.CameraConfigSubscriber` re-fetches camera config.
- `StorageServiceImpl` publishes `storage-config-changed` → `VmsFs.CameraStorageSubscriber` re-fetches storage assignments.

**Do not:**
- Add a direct gRPC call from service A to VmsCfg inside a hot path (e.g. per-request, per-packet) to read config that rarely changes — cache it and invalidate via pub/sub instead.
- Embed config values in event payloads as the authoritative state — events are notifications, not data carriers.

## Database access

Use **Entity Framework Core** for all database queries. Do not write raw SQL via `NpgsqlDataSource` / `NpgsqlCommand` in new code. Use `IDbContextFactory<T>` (or the injected `DbContext`) and LINQ for queries.

Exceptions where raw SQL is acceptable: complex bulk operations or SQL constructs that EF cannot express (e.g. `INSERT … ON CONFLICT DO UPDATE`), but keep them minimal and isolated inside repository methods.

## VmsOnvifActors — actors only

`VmsOnvifActors` hosts **only Dapr Actors** (currently `CameraActor` for PTZ). Do not add BackgroundServices, controllers, pub/sub subscribers, or any non-actor logic to this project. Business logic that needs ONVIF camera access but is not actor-based belongs in `VmsOnvif`.

## Docker image versions

In `docker-compose.yml` use `latest` for all third-party images. Do not pin specific version tags (e.g. `1.14.4`) — use `latest` instead.

## Protobuf — no `reserved` fields

When removing a field from a `.proto` message, delete it outright. Do not use `reserved` — field numbers are not reused anyway and `reserved` is just noise. Same applies to field name reservations.

## Database columns — no speculative additions

Only add columns that are actively used. Do not add `created_at`, `updated_at`, audit fields, or any other column "just in case" — if it is not read or queried by existing code, it does not belong in the schema. Add it when there is a concrete requirement.

## Square integration (future)

vms_rec будет встроено в основную систему **Square** (`../Square`, мониторинг объектов на карте) как
видеоподсистема. Интеграция пока **не реализована** — план зафиксирован в
[`docs/square-integration-plan.md`](docs/square-integration-plan.md). Любое дальнейшее проектирование
и развитие видеоподсистемы **не должно противоречить** этому плану. Соблюдай инварианты:

- **Автономность обязательна.** vms_rec всегда должен запускаться и работать полностью автономно.
  Интеграция со Square — опциональная фича, включаемая гейтом по env (нет sink'а Square → поведение
  standalone, без какой-либо зависимости от Square). Не вводи кода, который ломает автономный запуск.
- **VmsCfg — единственная точка подключения интеграции** (producer-сторона). Тяжёлое ядро (C++
  `media_server` и пр.) интеграцией не затрагивается. Не размазывай интеграционную логику по другим
  сервисам.
- **Конфиг — только снизу вверх.** Конфигурация камер принадлежит vms_rec и пушится в Square; наружу
  она read-only. Изменение состояния извне — **только командами** (actions: запись/арм/PTZ/`resync`),
  никогда не правкой конфига из Square.
- **root-объект интеграции = Dapr APP_ID** сервиса; команды — через Dapr service invocation,
  асинхронно (handle/uid сразу, статус позже).
- **Не конфликтуй с базисом интеграции:** общая шина pub/sub `orderpubsub`, общая docker-сеть
  Square `common_app_network`, раздельные PostgreSQL, модель самоописывающихся свойств/команд.
  Перед добавлением новых топиков, портов, env или сервисов сверяйся с планом, чтобы не создавать
  будущих коллизий.
