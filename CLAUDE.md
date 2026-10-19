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

`video_srv` is built with **C++20**. Prefer modern C++20 techniques and standard library facilities over legacy patterns whenever the result is clearer and safer (`std::span`, ranges, RAII, `std::optional`, `std::variant`, structured bindings, constexpr helpers, etc.). Do not introduce pre-C++20 workarounds where a direct C++20 solution is available.

## MediaMTX API access

All interaction with the MediaMTX REST API goes through **VmsUtils** (`vmsutils` Dapr app-id). No other service (video_srv, VmsCfg, web_srv, etc.) calls the MediaMTX API directly.

- video_srv sends `camera_id + stream_name` to VmsUtils via gRPC
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
consumers: this repo's `video_srv` (`vms` service) and the separate `video_a` repo's
`analytics-worker`, which builds `FROM ta-deps` via a sibling checkout (see `video_a/scripts/build.sh`).
It installs protobuf/grpc/spdlog/ffmpeg for video_srv plus openvino for video_a — when changing
its package list, consider both consumers.

`video_a`'s CI does not follow this repo: it builds against the `treealarm/ta-deps` tag written in
`video_a/ta-deps.tag`. After publishing a new `ta-deps` (`../ta_install/scripts/push-deps-images.sh`),
bump that file in `video_a`, in a commit of its own — until then video_a keeps building on the old
base, which is safe but means a package added here for video_a is not there yet.

**Adding an ffmpeg feature to the shared vcpkg tree breaks every stale build directory.**
`video_srv` and `video_a` configure against `/mnt/wdblue/vcpkg` in classic mode, and both
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

**`vaapi` is the exception, and it is in `ta-deps` deliberately.** video_srv transcodes live
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

## The boundary between `roitrc/` and `roitrc/ta_roienc/`

`roitrc/ta_roienc` is a submodule on github `treealarm/roi_transcode`, the `ta_roienc` library
(adaptive ROI encoding). That repository holds only the library's code and its C++ unit tests; it
deliberately knows nothing of gRPC, Dapr or protobuf. It may be edited — it is our library — but
everything that makes sense only for ta_vms lives here, not there.

**It has three consumers, and the second raises the cost of a mistake.** Besides `roitrc`, it is
linked by `video_srv` — live HEVC→H.264 transcoding for WebRTC goes through
`ta_roienc::Transcoder`, because libavcodec has no automatic choice of hardware and
`avcodec_find_encoder` always returns libx264 (see
`video_srv/writer/processors/hw_transcode_processor.cpp`).
**The default engine is `Engine::Vaapi`, not `Engine::Auto`**, and the difference is one of
meaning: the library is linked only for the hardware. If the hardware engine did not open — or an
`auto` chain set explicitly through `VMS_VIDEO_ENCODER` came down to a software one — the stream is
handed back to `compat_sink`, and `video_transcode_processor` takes it. That one has
`tune=zerolatency`, and the library's software engine does not: 34 frames in the pipeline against
4 through `h264_vaapi`, which is a second and a half of delay on live video.
The third is the separate `video_a` repository, which has its own `ta_roienc` submodule and links
only the decoder (`ta_roienc_decode`) for the analytics worker; a rename in the library's public
API has to be carried there too, and its submodule bumped in a commit of its own.

**A build without the submodule must keep working** — a clone made without `--recursive` has an
empty `roitrc/ta_roienc`, and that is a reason to warn, not to fail.
`video_srv/CMakeLists.txt` adds it only when `roitrc/ta_roienc` is checked out; otherwise it
warns, leaves `hw_transcode_processor` out (`VMS_WITH_TA_ROIENC` is undefined) and every HEVC
stream takes `video_transcode_processor`. Such a binary refuses to start with `VMS_VIDEO_ENCODER`
set, since it cannot honour it. `roitrc/CMakeLists.txt` warns and builds an empty `roitrc` target.
The image build passes `-DVMS_REQUIRE_TA_ROIENC=ON`, so an image can never lose the hardware path
silently — keep it that way. New code that touches `ta_roienc` from `video_srv` goes behind the
same switch.
A broken submodule bump takes live video down, not only the clip queue. Bump the version in a
commit of its own and check both paths.

`roitrc/` is everything about re-compression that is ours: the gRPC contract `VmsFs` calls, the job
model (it mirrors EdgeImport in `video_srv`), the Dapr notifications and the `ROI_ALLOWED_ROOTS`
path sandbox. The dependency arrow is one and narrow: only the public API is linked —
`ta_roienc/ta_roienc.h`, `ta_roienc/transcoder.h` and the headers they include — and nothing reaches into the
library's internals. If new code needs the encoder's `src/`, that is a sign it is on the wrong side
of the boundary.

The contract `proto/roi_transcode.proto` lives here, beside the others, in a single copy.

It is built by `roitrc/CMakeLists.txt` through `add_subdirectory(ta_roienc)` with
`TA_ROIENC_BUILD_TESTS` and `TA_ROIENC_INSTALL` off: the library's unit tests run in its own
repository. The service's own checks are `roitrc/scripts/check.sh` (startup with a broken
configuration, both gRPC entry points, the sandbox). The `roi-deps` base image is built from the
submodule's `docker/intel/`; `roitrc/Dockerfile` takes the repository root as its context, because
it needs both `roitrc/` and `proto/`.

Updating the library's version is an ordinary submodule bump, in a commit of its own, so that the
history shows which encoder went into a release.

## Source names and runtime names differ on purpose

The source tree was renamed — `media_server/` → `video_srv/`, `web_vms/` → `dotnet/` (every C#
service), `web_vms.Server/.client/.Types` → `web_srv.*` with their namespaces — but nothing a
running deployment knows a service by was. The C++ service is still the `vms` target, binary, Dapr
app id and image; the web server is still the compose service `web_vms`, the image
`treealarm/web_vms` and the app id `web-vms`, and the state store keys keep those prefixes.
Renaming any of these is a migration for every installation (`../ta_install`, Square's `.env`,
keys already in the state store), not a tidy-up — do not do it in passing.

## Adding a new service

When adding a new service (C# or C++), you **must** update all of the following:
1. `.env` — add the service's port and any Dapr app-id env vars
2. `../ta_install`: `docker-compose.yml` (image-based service + dapr sidecar), `.env`, and the image pair in `scripts/push-images.sh`
3. `docker-compose.yml` — add service + dapr sidecar entries (with `profiles: [app]`)
4. `.vscode/launch.json` — add a debug launch configuration for local development
5. `.vscode/tasks.json` — add `prepare:<name>`, `dapr:<name>:start`, `dapr:<name>:stop` tasks (copy pattern from vmslogger)
6. `dotnet/ta_vms.sln` — add the project (for C# services)

Omitting any of these causes a startup crash or missing debug config in dev.

## Environment variables

When adding a new required environment variable to any service:
1. Add it to `.env` (docker-compose local dev values).
2. Add it to `../ta_install/.env` (production/installation values).
3. Add it to **every** `launch.json` that launches that service — both the service-specific one (`dotnet/.vscode/launch.json`, `video_srv/.vscode/launch.json`) and the root `.vscode/launch.json`. Missing it in any one of these causes a startup crash in dev.

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
own subscription — so `server_id` is what lets a node-scoped subscriber (`video_srv`, `VmsOnvif`)
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

For a service that can be scaled horizontally (today: `web_srv.Server`), use a **version key in the
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

- `web_srv.Server` — stateless, scales by **replicas**. Keep it that way: no per-instance session
  tables (WHEP session state travels in an AES-GCM sealed token, see `WhepSessionToken`), no
  subscriptions, no local files.
- `video_srv` (`vms`) and `VmsFs` — scale by **sharding**: one pair per node, pinned to that
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
exposed via the `ListEntityStates` gRPC (consumed by `web_srv.Server`'s `/api/entitystate`
endpoint and the "Entity State" debug tab in the frontend).

**Publishing:**
- C# services: call `builder.AddVmsDebugState()` (from `VmsLog`), then inject
  `EntityStatePublisher` and call `Publish(entityType, entityId, state)`.
- C++ services: use `entity_state_publisher` (in `video_srv/grpc_layer`) which wraps
  `dapr_publisher::publish_json`.

## Maps & plans: positions belong to ta_vms

Spaces (maps and nested plans), plan images, camera placements and landmarks live in VmsCfg; see
[`docs/maps-and-plans.md`](docs/maps-and-plans.md). Invariants a change must keep:

- Spaces nest like Square's diagrams, but each has its own coordinates: a nested plan is never laid
  over its parent's map, its footprint there is only the way in.
- A position is WGS84 plus metres **above the floor of its space**, never above sea level, and is
  never stored in plan pixels. A plan in made-up coordinates uses made-up WGS84 around (0, 0) rather
  than a second coordinate system.
- Two spaces share geometry only when they are the same space or both have real coordinates; every
  cross-space feature (calibration, aiming, master/slave) checks that rule.
- A camera has at most one **reference** placement — where it really stands, the only one aiming
  uses — and any number of pictures (Square's `owner_id`). Another placement is never promoted to
  reference behind the administrator's back.
- Heading is a compass bearing, 0 = north, clockwise (Square's `__image_rotate`).
- Calibration fits are never stored — raw observations and ground marks in VmsCfg, the PTZ fit and
  the fixed camera's ground homography in `VmsPtzTrackingSingleton/Geo`, as with the slave binding.
  The browser only multiplies by the matrix it is handed; it never fits.
- Detections on the map come from the existing live stream (`api/analytics/live`), projected in the
  browser — no second detection stream for maps.
- Space changes are announced on `space-config-changed {"space_id"}`; a camera's placements or
  observations on `server-config-changed`.
- Spaces reach Square only through `VmsSquareSingleton` (`SquareSpaceExport`): each plan a Square
  diagram, each picture placement a replica with `owner_id` = the camera. Nothing about Square goes
  into VmsCfg.
- Map tiles reach browsers only through web_srv's caching proxy (`api/map/tiles`), cached in the
  state store — never on local disk and never fetched by the browser from the provider directly.

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

ta_vms is built into the main **Square** system (`../Square`, monitoring objects on a map) as its
video subsystem. Login and the basic Phase 1/2 (cameras on the map, properties, alarms, the
"Monitor" tab in Square) are implemented; the plan is
[`docs/square-integration-plan.md`](docs/square-integration-plan.md). Any further design and
development of the video subsystem **must not contradict** that plan. Keep these invariants:

- **The docker network shared with Square is mandatory, but there is no ta_vms data in other
  projects' containers any more.** ta_vms **always** lives in the `common_app_network` network (the
  same `name:` as Square's/`multitenant_admin`'s, but `external: true` — the network is created by
  Square/`multitenant_admin`, which have to come up first; ta_vms's own key for the network is
  `default`, theirs is `shared_network`, and compose refuses to adopt another project's network
  whose key in the `com.docker.compose.network` label does not match). It is needed **only** for
  Dapr service invocation into Square and app-id resolution through mDNS.
  All the state and the whole bus are ta_vms's own: the **state store** is its own PostgreSQL
  (database `POSTGRES_STATE_DB`, component `init_files/dapr_components/statestore.yaml`), the
  **pub/sub** its own `rabbitmq` (`init_files/dapr_components/orderpubsub.yaml`). Another
  project's `redis` belongs in neither: it is declared without a volume, and when databases are
  stamped out per tenant, a shared Redis with `keyPrefix: "name"` would give every tenant the same
  `cam-perm-version` key. Separately about Redis: two different containers with the same alias
  `redis` in one network make Docker answer DNS queries with a random round-robin between them
  (verified empirically) — so keeping our own Redis beside another one was impossible, and that is
  one of the reasons the broker became RabbitMQ (the second: Redis does not install natively on
  Windows, and a standalone installer is planned).
  Features not related to the integration (login, push to Square) are gated by env
  (`KEYCLOAK_URL`, `SQUARE_INTEGRATION_SINK_APP_ID`) — network connectivity is unconditional, not
  behind a gate.
- **`VmsSquareSingleton` is the only point where the integration plugs in** (the producer side).
  The heavy core (the C++ `video_srv` and the rest) is not touched by the integration. Do not
  spread integration logic over other services: `VmsCfg` knows nothing about Square at all and only
  publishes its ordinary events (`server-config-changed`, `camera-state-changed`), which the
  integrator subscribes to.
- **The producer's identity in Square's tree is `SQUARE_INTEGRATION_PRODUCER_ID`, not the
  process's `APP_ID`.** The root object's id is derived from this value deterministically, so both
  sides set it by configuration and exchange nothing at runtime. The value is permanent: changing
  it orphans the whole subtree already registered under the old root.
- **Configuration flows bottom-up only.** Camera configuration belongs to ta_vms and is pushed to
  Square; outside it is read-only. State is changed from outside **only by commands** (actions:
  recording/arming/PTZ/`resync`), never by editing configuration from Square. In Square the
  property panel of integration objects (those with `i_name`) is read-only, and no type hierarchy
  is registered for them (`UpdateIntegroTypes`), so they cannot be created by hand from Square's
  tree.
- **The integration's root object = `SQUARE_INTEGRATION_PRODUCER_ID`** (the historical value is
  `vmscfg`, from when `VmsCfg` was the producer); commands go through Dapr service invocation,
  asynchronously (a handle/uid at once, the status later). The command handlers are not wired up
  yet.
- **Do not conflict with the integration's basis:** the shared `common_app_network` docker network
  and Dapr service invocation over it (the sides exchange no messages over a bus:
  `VmsSquareSingleton` subscribes only to ta_vms's own topics and reaches Square by calls — which
  is why ta_vms's bus is its own, RabbitMQ, and the component name `orderpubsub` matches Square's
  only historically), separate PostgreSQL and separate brokers, the model of self-describing
  properties/commands. Before adding new topics, ports, env variables or services, check them
  against the plan so as not to create collisions later.
