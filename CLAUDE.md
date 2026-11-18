# Architecture rules

These rules override any default behaviour. The ones that apply to every change are stated in full
here; a rule that applies only when you touch a particular area is one short statement plus a link
to the document that holds the full version — read that document before changing the area. All the
documents are indexed in [docs/README.md](docs/README.md). Keeping this file short is deliberate: a
rule that is always visible is a rule that is always followed.

## Language conventions

Chat communication with the user is in Russian. Code — comments, identifiers, commit messages, log messages, docs — is always in English.

Everything addressed to the user rather than to the codebase follows the chat, not the code: **implementation plans (including plan files) and code-review findings are written in Russian.** The code they talk about stays English, so quoted identifiers, file paths and code snippets are of course unchanged.

## Session handoff: propose the next prompt

A long session carries every earlier turn with it, so a different task starts in a fresh one: it reads the repository, the plan files and `MEMORY.md` instead of the chat, and a commit is what it starts from. When a task is finished and the next one is different, say so and give the user a ready-to-paste prompt for that new session — naming the task, the branch to cut from `master`, and the plan file or starting point.

The exception is when the user has said they are leaving and the work is to go on without them: then keep working in the same session, ask nothing, take the reasonable default where a choice comes up, and record that choice in the commit message or the plan.

## Engineering principles (mandatory)

For all new code and refactors, treat these as hard constraints rather than style preferences:

- **SOLID:** keep single responsibility boundaries clear, design against interfaces/abstractions, and avoid coupling unrelated concerns in one class/module.
- **KISS:** prefer the simplest design that meets the requirement; avoid speculative abstractions and unnecessary indirection.
- **YAGNI:** do not implement features, extension points, or configuration knobs before there is a concrete requirement.
- **DRY:** avoid duplicated business logic and duplicated protocol/format conversions; extract shared helpers when two paths must stay behaviorally identical.

`video_srv` is built with **C++20**. Prefer modern C++20 techniques and standard library facilities over legacy patterns whenever the result is clearer and safer (`std::span`, ranges, RAII, `std::optional`, `std::variant`, structured bindings, constexpr helpers, etc.). Do not introduce pre-C++20 workarounds where a direct C++20 solution is available.

## Test code: name the contract, do not spell out its position

A test is read as the statement of a contract, so a bare number or a bare string hides the
contract instead of stating it:

- **Entity names and domain values in a test are lowercase** (`"city"`, `"floor plan"`,
  `"camera-1"`).
- **A string that occurs more than once in a test file is a named `const`** — a space name, a user,
  a role, an action name, a protocol string, a name prefix. One occurrence is a literal; two are a
  value waiting to be written twice.
- **A page size, a limit or a count is a named constant**, used both in the call and in the
  assertion (`const int pageSize = 2;` then `count = pageSize`, `ordered.Take(pageSize)`). Changing
  the size then keeps the test meaningful instead of silently breaking it.
- **An assertion names the thing it checks, not its position**: `page.Objects.Keys.Last()` rather
  than `ordered[3]`, `ordered.Skip(pageSize)` rather than `ordered.Skip(2)`, `messages.First()`
  rather than `messages[0]`.
- **Prefer the typed value where one exists** — a `Guid` for an identifier, a DTO property for a
  field — over repeating its wire form.

## MediaMTX API access

All interaction with the MediaMTX REST API goes through **VmsUtils** (`vmsutils` Dapr app-id). No other service (video_srv, VmsCfg, web_srv, etc.) calls the MediaMTX API directly.

- video_srv sends `camera_id + stream_name` to VmsUtils via gRPC
- VmsUtils resolves the assigned MediaMTX server (with its `api_url`) from VmsCfg, then queries the MediaMTX REST API
- Other services consume the results from VmsUtils — they do not hold or forward MediaMTX API URLs

## Deployment lives in ../ta_install

Production/standalone deployment is a **separate repository** (`../ta_install`, github
`treealarm/ta_install`): the product compose (prebuilt `treealarm/*` images, own broker), its
`.env`, `init_files/` and the image build/push scripts. (The analytics models are
not fetched at deploy time: `video_a` bakes them into its image, and its CI takes them from
`treealarm/analytics-models`, published by `video_a/scripts/push-models-image.sh`.) This repo's
root `docker-compose.yml` is **dev only** (build-from-source).
Never re-grow a prod compose inside ta_vms.

## Findings for later: docs/future_plans

A problem found while doing something else, which needs doing but not next, goes into
`docs/future_plans/` — one file per topic, listed in its README — rather than only into a chat or a
commit message. When work on it starts, the file is deleted or cut down to what is left.

## ta-deps is shared with video_a

`ta-deps` is the C++ toolchain + vcpkg base image for **two** consumers: this repo's `video_srv`
(`vms`) and the `video_a` repo's `analytics-worker`, which builds `FROM ta-deps` through a sibling
checkout. When changing its package list, consider both. After publishing a new `ta-deps`, bump
`video_a/ta-deps.tag` in a commit of its own. Adding an ffmpeg feature to the shared vcpkg tree
breaks every stale build directory — delete the build directory and reconfigure.
Full statement: [docs/architecture/ta-deps.md](docs/architecture/ta-deps.md).

## The boundary between `roitrc/` and `roitrc/ta_roienc/`

`roitrc/ta_roienc` is a submodule on github `treealarm/roi_transcode`, our `ta_roienc` library. It
has three consumers: `roitrc`, `video_srv` (live HEVC→H.264 for WebRTC) and `video_a`; only its
public API is linked, and nothing reaches into its `src/`. A build without the submodule must keep
working — warn, leave `hw_transcode_processor` out, never fail. Rename in its public API and bump
the submodule in commits of their own, checking both paths. Do not build Windows `vms.exe` without
the library — the hardware engines arrive through it. Its default engine is hardware only.
Full statement, including the Windows engines and the vaapi rules:
[docs/architecture/roi-encoder-boundary.md](docs/architecture/roi-encoder-boundary.md).

## Source names and runtime names differ on purpose

The source tree was renamed — `media_server/` → `video_srv/`, `web_vms/` → `dotnet/` (every C#
service), `web_vms.Server/.client/.Types` → `web_srv.*` with their namespaces — but nothing a
running deployment knows a service by was. The C++ service is still the `vms` target, binary, Dapr
app id and image; the web server is still the compose service `web_vms`, the image
`treealarm/web_vms` and the app id `web-vms`, and the state store keys keep those prefixes.
Renaming any of these is a migration for every installation (`../ta_install`, keys already in the
state store), not a tidy-up — do not do it in passing.

## Adding a new service

When adding a new service (C# or C++), you **must** update all of the following:
1. `.env` — add the service's port and any Dapr app-id env vars
2. `../ta_install`: `docker-compose.yml` (image-based service + dapr sidecar), `.env`, and the image pair in `scripts/push-images.sh`
3. `docker-compose.yml` — add service + dapr sidecar entries (with `profiles: [app]`)
4. `.vscode/launch.json` — add a debug launch configuration for local development
5. `.vscode/tasks.json` — add `prepare:<name>`, `dapr:<name>:start`, `dapr:<name>:stop` tasks (copy pattern from vmslogger)
6. `dotnet/ta_vms.sln` — add the project (for C# services)
7. `../ta_install/windows/config/services.json` — the Windows supervisor's manifest (app id, exe,
   ports, sidecar ports, placement, and `roles`: a system-wide service runs on `standalone` and
   `core`, a node's has no `roles` and `${TAVMS_NODE_SUFFIX}` in its app id), unless the service
   deliberately does not ship on Windows; then say so in `../ta_install/windows/README.md`.
   `scripts/Build-Stage.ps1` fails when the manifest names an exe the ta_vms Windows build does not
   contain. A node's service goes into `../ta_install/docker-compose.distributed.yml` as well.

Omitting any of these causes a startup crash or missing debug config in dev.

## Environment variables

When adding a new required environment variable to any service:
1. Add it to `.env` (docker-compose local dev values).
2. Add it to `../ta_install/.env` (production/installation values).
3. Add it to **every** `launch.json` that launches that service — both the service-specific one (`dotnet/.vscode/launch.json`, `video_srv/.vscode/launch.json`) and the root `.vscode/launch.json`. Missing it in any one of these causes a startup crash in dev.
4. If its value differs on Windows (a host name, a path, a port the dynamic range could reserve),
   add the Windows value to `../ta_install/windows/config/windows.env`. The installer ships
   `.env` as the base and applies only these differences on top, so a variable whose Docker value
   also works there needs nothing more.

**No silent fallbacks.** If an environment variable is required, the service must throw (or return non-zero) immediately on startup when it is missing — never silently default to a hardcoded value. A missing required variable is a misconfiguration and must be visible immediately, not discovered at runtime through unexpected behavior.

- C# pattern: `Environment.GetEnvironmentVariable("X") ?? throw new InvalidOperationException("Required environment variable 'X' is not set")`
- C++ pattern: use `require_env("X")` (which throws `std::runtime_error`)
- Exception: genuinely optional variables that enable/disable a feature (e.g. `APP_ID` in VmsFs — omitting it disables multi-instance scoping). Document the default behavior in a comment when you use an optional variable.

## Inter-service communication: config changes via pub/sub

When a service needs to react to a configuration change that originated in another service, use the **pub/sub pattern** — do not poll or call the source service directly on every request.

**Rule:** VmsCfg is the single source of truth for all configuration (cameras, storages, recording profiles, MediaMTX servers, etc.). Any other service that caches or acts on this configuration must subscribe to change events published by VmsCfg, then fetch the updated data itself. The map's tree — spaces, plans, placements, landmarks and calibrations — is VmsMap's own and not VmsCfg configuration: its changes travel on `map-config-changed` (see "Maps & plans").

**Exception: a logical device object's behaviour is VmsMap's.** The arming mode of a camera or a digital input (`inherit / armed / disarmed`) and an input's alarm rule (`trigger_on`, `alarm_enabled`) are not hardware configuration: the map is their only consumer — the arming resolver, the alarm rule and the operator's list read them, and the map derives the controller's decision from them. They live in `device_object_settings` in `ta_map`, and VmsCfg keeps only what services near the hardware read: the camera's **effective** `armed` flag, which the map writes. Do not add a second copy back to VmsCfg's `camera_states.arm_mode` or `device_settings`; the old columns there are a migration source for release D1 only and go away in D2 (`docs/future_plans/23-remove-vmscfg-legacy-arming.md`). The device's display name stays in `device_settings`: it is the device's identity, not its behaviour.

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
outside its own node — a system-wide service such as `VmsSingleton` does — would then have to
know every server id in the deployment just to subscribe.

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
1. The owner writes a new value to a well-known key after the write succeeds (`rights-version` is
   VmsMap's, see `DaprRightsVersionNotifier`; `cam-node-version` is VmsCfg's) — the value is
   meaningless, only "different from last time" matters.
2. The consumer re-reads that key at most once per short interval and drops its cache when it moved
   (`RightsService.EnsureFreshAsync`), keeping its own TTL as the backstop for a state store
   outage.

Do not "fix" this with a per-instance `consumerID`: it works, but leaves an orphaned queue behind on
every restart — one per instance that ever started, each still bound to the topic exchange and each
accumulating messages nobody reads.

## Scaling model and deployment layout

A service's scaling class is in its name and is a hard constraint: `web_srv.Server` — replicas;
`video_srv` (`vms`) with `VmsFs`, `VmsOnvif`, `VmsOnvifActors`, `VmsLogger` and `roitrc` — sharded
by node; `VmsSingleton` and `VmsAnalyticsSingleton` — exactly one instance, ever; `VmsCfg`,
`VmsUtils` and `VmsMap` — replicable. Nothing may tie a service to "everything is on this machine":
addresses and ports come only from env, and an app listens on the loopback only.
The core-and-nodes layout, the node registry and enrollment, Consul/Sentry/mTLS, the distributed
env package: [docs/architecture/distributed.md](docs/architecture/distributed.md).

## Debug "entity state" presence registry

Any service may publish a periodic snapshot of an entity's in-memory state to the
`debug-entity-state` topic — best-effort, no history, nothing written to a database. VmsSingleton
keeps the registry and `ListEntityStates` serves it. C# services: `builder.AddVmsDebugState()` plus
`EntityStatePublisher`; C++ services: `entity_state_publisher`. Envelope and staleness rules:
[docs/architecture/debug-entity-state.md](docs/architecture/debug-entity-state.md).

## Maps & plans: positions belong to ta_vms

Spaces (maps and nested plans), plan images, camera placements, landmarks and their calibrations
live in **VmsMap**, on Square's object tree; VmsCfg keeps the cameras, and a placement write checks
the camera exists through `GetCamera`. These are the invariants a change must keep: one tree, each
space with its own coordinates and WGS84 plus metres above the floor of its space, one **reference**
placement per camera, `owner_id` naming an original one level deep, heading as a compass bearing,
and calibration fits never stored. The model, the storage and the events:
[docs/maps-and-plans.md](docs/maps-and-plans.md). States, the journal and rights:
[docs/maps/states-and-alarms.md](docs/maps/states-and-alarms.md).

## Login: Keycloak everywhere, the standalone installs bring their own

web_srv logs users in with Keycloak's password grant, server-side, so `KEYCLOAK_URL` only has to be
reachable from web_srv. Realm roles are the whole model: `admin` administers everything, anyone else
sees the tree nodes granted to their `preferred_username` or a realm role. Rights are rows on the
tree in VmsMap, inherited downwards; `[AllowAnonymous]` belongs on actions, never on a controller.
Details: [docs/architecture/login.md](docs/architecture/login.md).

## Database access

Use **Entity Framework Core** for all database queries. Do not write raw SQL via `NpgsqlDataSource` / `NpgsqlCommand` in new code. Use `IDbContextFactory<T>` (or the injected `DbContext`) and LINQ for queries.

Exceptions where raw SQL is acceptable: complex bulk operations or SQL constructs that EF cannot express (e.g. `INSERT … ON CONFLICT DO UPDATE`), but keep them minimal and isolated inside repository methods.

### Creating a database: name it in `.env`, nothing else

A database is created by exactly one thing per platform — `scripts/prepare-databases.sh` in Docker,
`TaVmsHost` on Windows — and both create every database named by a `POSTGRES_*_DB` variable in the
environment. So a new service's database is added by putting `POSTGRES_<NAME>_DB` into `.env` (and
`../ta_install/.env`), and by nothing else. Never `docker-entrypoint-initdb.d`, never a hardcoded
list of names. Why: [docs/architecture/database.md](docs/architecture/database.md).

## VmsOnvifActors — actors only

`VmsOnvifActors` hosts **only Dapr Actors** (currently `CameraActor` for PTZ). Do not add BackgroundServices, controllers, pub/sub subscribers, or any non-actor logic to this project. Business logic that needs ONVIF camera access but is not actor-based belongs in `VmsOnvif`.

## Docker image versions

In `docker-compose.yml` use `latest` for all third-party images. Do not pin specific version tags (e.g. `1.14.4`) — use `latest` instead.

This is a rule for compose only. The Windows installer carries the binaries themselves, so every
third-party component it ships is pinned, by URL and hash, in `../ta_install/windows/versions.json`.

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

## Infrastructure of its own

ta_vms joins no other project's docker network and keeps no data in another project's container:
its own PostgreSQL state store and its own RabbitMQ. App ids resolve over mDNS in Docker; the
single-machine Windows install uses Dapr's `sqlite` resolver instead.
Why: [docs/architecture/distributed.md](docs/architecture/distributed.md#infrastructure-of-its-own).

## Square merges into ta_vms

Square (`../Square`), the map-based monitoring platform ta_vms used to plug into as a video
subsystem, is being folded into ta_vms as its map and events layer: Square's server core becomes
a map service of ta_vms, its object tree, states with alarms bubbling to the parent, event journal
and mnemonic diagrams with it. The integration was removed first (`VmsSquareSingleton`, the
`SquareIntegrationClient` submodule, the shared `common_app_network`). The plan, by milestones, is
[`docs/plans/square-merge/`](docs/plans/square-merge/README.md); new work on maps, object states,
events or permissions must not contradict it. Two of its rules already hold:

- **The map is a layer; the heavy core does not know about it.** The map service learns from the
  ordinary events (`server-config-changed`, `camera-state-changed`, `retention-event-created`) and
  reads the state back from its owner; nothing in `video_srv`, VmsFs, VmsOnvif or VmsCfg is written
  for the map's sake.
- **No backward compatibility with Square** (user, 2026-10-28: neither product is in use yet).
  Square's code is taken where it is good and refactored where it is not; its general-purpose
  integration of foreign systems (integration types, self-describing actions, the producer
  protocol) is not carried over.
