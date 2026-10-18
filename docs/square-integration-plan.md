# Integrating ta_vms into Square: a typelib client, with VmsCfg as the producer

> **Update (2026-08-10).** The producer moved out of `VmsCfg` into a service of its own,
> `VmsSquareSingleton`: `VmsCfg` became a replicable, stateless interface to the database and knows
> nothing about Square — the integrator subscribes to its ordinary events. The producer's identity
> is no longer derived from the process's `APP_ID` but set by `SQUARE_INTEGRATION_PRODUCER_ID` (the
> value `vmscfg` is kept, so the tree already registered stays where it is). The model below — a
> root object, self-describing properties, commands through service invocation — is unchanged; what
> changed is only which process plays the producer and where its name comes from.


> Status: **a plan for the future, not implemented.** The document is a guide for how the video
> subsystem develops. Current development must not contradict this plan (see the "Square
> integration" section in `CLAUDE.md`). The integration itself is done later, as a separate task.

## Context

`ta_vms` is a distributed VMS (recording and streaming built on MediaMTX).
Square (the neighbouring project `../Square`) is the main system for monitoring objects on a map
(Leaflet). They are related projects from one vendor with an almost identical stack: **Dapr + a
message broker + PostgreSQL + .NET 10 + gRPC + React** (Square's broker is Redis, ta_vms's is
RabbitMQ; see below — the two sides exchange no messages). ta_vms has to be built into Square in a
way that **still lets ta_vms run fully on its own**.

Decisions agreed:
- **Depth:** in stages, eventually everything (events/commands → video in the UI → single sign-on).
- **Configuration/cameras:** ta_vms owns the functional configuration; configuration is **pushed
  bottom-up** (ta_vms → Square) and is read-only in Square. Square owns the "presentation" (name,
  grouping, the position of cameras not placed in ta_vms); the position of a camera that ta_vms
  places in a space with real coordinates belongs to ta_vms (`docs/maps-and-plans.md`).
  State is changed from Square **only by commands**.
- **Infrastructure:** a shared docker network, **separate PostgreSQL** and **separate brokers** —
  ta_vms keeps its Dapr state store in its own PostgreSQL, and its own bus (RabbitMQ) too. There
  is no shared bus and none is needed: the sides talk by calls, not by messages.
- **Shape of the layer:** Square hosts the integration with an extensible **gRPC + Dapr** API
  (+ REST). The producer is a reusable **typelib client** (like `IntegrationUtilsLib`, but on the
  providing side), which `VmsCfg` plugs in. The protocol is **Square's own protocol** (not a
  neutral one).
- **Coupling:** Square is reworked freely; in ta_vms **only `VmsCfg`** is touched (plugging in the
  typelib), and running on its own is kept by **a gate on an env variable** (no Square sink → it
  behaves as before). The heavy core (the C++ video_srv and the rest) is not touched.
- **Object model:** the root object = the process's **Dapr APP_ID** (`vmscfg`); its children are the
  cameras. The tree is `vms server (APP_ID) → camera`. Properties are self-describing and
  read-only. Commands are typed actions.
- **Lifecycle:** deleting the root in Square → **cascading deletion of the children and the
  integration broken off**; individual objects can be **taken out of the integration in admin
  mode** (per-object disable).
- **Commands/triggers:** through **Dapr service invocation on the APP_ID, asynchronously** — a
  command returns a handle (uid) at once and the status arrives later (like `ProtoActionExe.uid` +
  `UpdateActionResults` in `Square/Protos/integro.proto`). Re-pushing the configuration is a
  `resync` command on the root.

Both sides declare a pub/sub component with the same name, `orderpubsub`, but that is a
coincidence of names, not a shared bus: `VmsSquareSingleton` subscribes only to ta_vms's own
topics and reaches Square through Dapr service invocation. So exactly one thing is needed from
shared infrastructure — the shared docker network, so that APP_ID resolution through Dapr's mDNS
name resolution works.

---

## Architecture of the integration layer

```
            typelib SquareIntegrationClient (gRPC+Dapr, behind the ISquareIntegration interface)
  VmsCfg ──register root(APP_ID)/push objects+props/report action status──►  Square (integration API)
 (ta_vms) ◄──ExecuteAction(uid)/resync  (Dapr service invocation on app-id vmscfg)──
```

### 1) Square — the integration host (an extensible API, Square's protocol)
Square's raw protocol is brought to a settled form on its **gRPC + Dapr** API
(`Square/Protos/integro.proto`, `actions.proto`; the consumer-side glue in
`Square/IntegrationUtilsLib/IntegrationSyncFull.cs`, rendering in `tree_alarm.client/src/actions/
ActionsControl.tsx`):
- accepting the root object by APP_ID and its subtree of cameras (`IntegroService.UpdateIntegro` +
  `IntegroTypesProto`/`InitTypes`: types `vms` → `camera`);
- accepting read-only properties (`ProtoObjExtraProperty` → `UpdateProperties`);
- a registry of the commands available per object and invoking them
  (`ActionsService.GetAvailableActions`/`ExecuteActions`), accepting statuses
  (`UpdateActionResults`);
- **cascading deletion** of the subtree when the root is deleted; **per-object disable** in admin
  mode;
- **removing** the old two-way path of creating configuration from Square
  (`Topics.OnUpdateIntegros`/`OnDeleteIntegros`) — leaving Square only the presentation and the
  commands.

### 2) The `SquareIntegrationClient` typelib (implemented, producer side)
A thin client over the Square API, **gRPC + Dapr service invocation**, behind the
`ISquareIntegration` interface (SOLID, and replaceable):
- `UpsertObjects`/`PushFigures`/`PushProperties`/`PushStates`/`PushEvents`/`PushTracks`/`PushValues`/
  `RegisterIntegro`/`RegisterIntegroTypes`/`ReportActionStatus`/`GetObjects`/`GetProperties`/
  `GetIntegroByType`/`GetIntegroByIds`/`GenerateObjectId` — producer → Square, and reading back.
- The incoming side: `IObjectActions`/`ActionsServiceImpl` — hosting the
  `GetAvailableActions`/`ExecuteActions`/`ExecuteActionGetResult`/`CancelActions` handler, which
  Square calls by Dapr service invocation on the producer's APP_ID.
- **A no-op implementation** of `ISquareIntegration` for running on its own (no sink set) —
  guarantees that without the integration the producer works standalone.
- **Where it lives: a repository of its own, `github.com/treealarm/SquareIntegrationClient`,
  included as a git submodule** — `ta_vms/dotnet/SquareIntegrationClient` and
  `Square/SquareIntegrationClient` (after the example of `KeycloakAdmin`, which is already a
  submodule in both repositories). It holds the `.proto` contract
  (`integro`/`tracks`/`actions`/`common`/`diagrams`) and the generated gRPC stubs — one source of
  truth instead of copies kept in step by hand. It is used by **every** producer of the protocol:
  the external one (`ta_vms/VmsCfg`) and Square's internal producers (`AASubService`,
  `GrpcTracksClient`). The host side (`Square/IntegrationHost`, `Square/IntegrationServerLib`,
  `Square/IntegrationUtilsLib/IntegrationSync*.cs`, tied to `Square/Domain`) does not move; it
  refers to the submodule only for the proto types.

### 3) VmsCfg — the first producer
`dotnet/VmsCfg` (`Program.cs`, `Grpc/CfgServiceImpl.cs`) plugs in the typelib and, **if the
integration is turned on by an env flag** (e.g. `SQUARE_INTEGRATION_SINK_APP_ID`):
- at startup: `RegisterRoot("vmscfg")` + a full push of the camera tree and properties from its
  DbContext;
- on its own events `server-config-changed`/`camera-state-changed`
  (already published in `CfgServiceImpl`) — pushes the deltas;
- declares the commands: camera — `start/stop_recording`, `arm/disarm`, (later) PTZ; root —
  `resync`;
- implements the command handlers through the existing internal cfg logic / calls to other ta_vms
  services (`CameraControlService.SetCameraState` in `proto/vms_control.proto`, `web_srv`'s
  `CameraStateController`, PTZ in `proto/onvif.proto`).
- **Running on its own:** flag not set → the typelib is a no-op and VmsCfg behaves exactly as now.
  This is in line with the CLAUDE.md rule "no silent fallback": the integration is an explicit,
  optional, documented feature.

### Properties and commands (self-description)
- **Camera properties (RO):** `online`, `recording`, `armed`, `profile_name`, `last_alarm_ts`,
  `live_url`, `has_archive`, `disk_usage`. Square renders them as states without interpreting them.
- **Commands:** only switching state (recording/arming/PTZ) + `resync`. No substantial
  configuration is changed by commands.
- **Ownership boundary:** functional configuration belongs to ta_vms (RO in Square); name and
  grouping belong to Square. Position belongs to ta_vms when the camera's reference placement lies
  in a space with real coordinates (the point and the heading go to Square on every push);
  otherwise to Square: the camera gets a default point once, at its first registration, and after
  that only the name and the properties are sent. The exception to "grouping belongs to Square": a
  placed camera hangs in Square's tree under its world — ta_vms's worlds are exported to Square as
  diagrams (a plan is a diagram with a picture, a camera's picture is an object with `owner_id`).
  Unplaced cameras stay under the root, as before. See `docs/maps-and-plans.md`, "Export to
  Square".

---

## Phase 0 — Plumbing: a shared network + cross-stack Dapr (done)

> Implemented differently from the original plan (an overlay plus alternative Dapr components) —
> below is the actual, simpler scheme. **Important:** `docker compose up` in ta_vms alone, without
> Square/multitenant_admin up, does not work — but now only because of the network (`external:
> true`, created by their stack). No ta_vms data is left in other projects' containers: both the
> state store and the bus are its own.

No separate overlay file and no alternative Dapr components turned out to be needed:
- `ta_vms/docker-compose.yml` renames its implicit `default` network to `common_app_network`
  (`networks: default: name: common_app_network`, with `external: true`). Every ta_vms service ends
  up in the same network as Square/multitenant_admin without a single change to an individual
  service. `external: true` is mandatory here: ta_vms's own key for the network is `default`,
  Square's/multitenant_admin's is `shared_network`, and compose writes the key into the
  `com.docker.compose.network` label and refuses to adopt a network whose label has another value
  ("network common_app_network was found but has incorrect label …"). The consequence: the network
  is created by Square/multitenant_admin, and their stack has to start first.
- ta_vms's own `redis` service was removed altogether, and for a while ta_vms's bus ran on
  multitenant_admin's `redis`. **The reason, not just style:** verified empirically — if two
  different containers in one network both register the alias `redis`, Docker answers DNS queries
  with a random round-robin between them; keeping its own `redis` next to another one under the
  same name in one network is impossible, it would mean a constant, unpredictable loss of part of
  the pub/sub messages. **Later the bus left Redis entirely** — `init_files/dapr_components/
  {,docker/}orderpubsub.yaml` is now `pubsub.rabbitmq` on ta_vms's own `rabbitmq`. The alias
  collision disappears by itself (the name `rabbitmq` is not taken in the other stacks), and the
  main motive is the deployment roadmap: docker → Kubernetes → a standalone installer without
  Docker, and there is no native Redis build for Windows. The bus semantics did not change:
  `consumerID` defaults to the app id, and Dapr sets up a fanout exchange per topic and a durable
  queue per (app id, topic) pair — exactly what a Redis Streams consumer group gave before.
- **ta_vms's state store did not move to the shared `redis`, and was later taken off it
  entirely** — `init_files/dapr_components/{,docker/}statestore.yaml` is now `state.postgresql` on
  ta_vms's own PostgreSQL (the `POSTGRES_STATE_DB` database). Two reasons: `multitenant_admin`'s
  `redis` has no volume, while state such as `onvif-observed-topics-<cameraId>` (TTL 10 days) must
  survive a restart; and when databases are stamped out per tenant, a shared `redis` with
  `keyPrefix: "name"` would give every tenant one and the same `cam-perm-version` key, while a
  separate database keeps them apart for free.
- `dapr-placement` stays separate (only actors need it; service invocation goes through mDNS name
  resolution in the shared network, not through placement).

`vmscfg`/VmsCfg reaches Square only through Dapr **service invocation** (a gRPC call to the
`integrationhost` app id) — no pub/sub is used between the stacks at all. That is why moving
ta_vms's bus to its own RabbitMQ did not affect the integration: the only shared infrastructure
left is the network.

## Phase 1 — Typelib + the Square API + the VmsCfg producer
1. Finish the Square integration API (section "1) Square — the integration host"):
   protocols/consumer/cascade/disable; remove the old path of creating configuration from Square.
2. Implement the `SquareIntegrationClient` typelib + `ISquareIntegration` (+ no-op).
3. Plug the typelib into `VmsCfg`: register the root by APP_ID, push the tree and properties,
   command handlers, the env gate.

Files (implemented for the protocol layer — see item 2 above): `github.com/treealarm/
SquareIntegrationClient` (a new repository, a submodule in `ta_vms/dotnet/SquareIntegrationClient`
and `Square/SquareIntegrationClient`); `Square/IntegrationUtilsLib/{SquareIntegration,Utils,
IntegrationSync,IntegrationSyncFull}.cs` (host side, trimmed), `Square/GrpcDaprLib.csproj`
(`integro`/`tracks`/`actions` taken out of the compilation), `Square/{AASubService,GrpcTracksClient,
ValhallaLib,IntegrationServerLib,LeafletAlarms}` (references to the submodule);
`ta_vms/dotnet/VmsCfg/Program.cs` + `Grpc/{CfgServiceImpl,SquareCameraPush,SquareAlarmSubscriber,
SquareResyncService}.cs`. Not done yet: commands (hosting `ActionsServiceImpl` in VmsCfg), the env
in `.env`/`dockerhub/.env`, the launch.json files (see ta_vms's CLAUDE.md on where new env
variables and services must be added).

## Phase 2 — Video in Square's UI
ta_vms already serves WebRTC live and the HLS archive through `web_srv` — nothing needs to be
written in ta_vms.
- A player in `Square/tree_alarm.client/src/`: builds the URL to `web_srv` from the
  `live_url`/`vms_camera_id` property. The base URL comes from the frontend's configuration.
- Access from the browser: `web_srv` behind a reverse proxy on the same origin as Square (or CORS).
- Clicking a camera → live; from an alarm or the timeline → the HLS archive at the right `ts`.

## Phase 3 — Single sign-on (Keycloak) at the edge
JWT validation goes **in front of** `web_srv` (a reverse proxy/gateway: nginx + oauth2-proxy or
Square's gateway), checking Square's Keycloak tokens. Inside the network `web_srv` has no
authorization, and it cannot be reached directly from outside. Reference:
`Square/LeafletAlarms/Authentication/ConfigureAuthentificationServiceExtensions.cs`.

> **Implemented (partly, ahead of time):** login and the `admin`/`user` roles are done — but JWT
> validation sits **inside** `web_srv.Server` (as in `LeafletAlarms` itself, rather than in a
> separate gateway), directly through `JwtBearerOptions.Authority`/OIDC discovery, with no separate
> reverse proxy. It uses **the same realm `myrealm`** as Square (a shared login; no separate users
> were created for ta_vms). It is turned on optionally by `KEYCLOAK_URL` (empty → standalone,
> without authorization, as before) — see `dotnet/web_srv.Server/Authentication/`,
> `Controllers/AuthController.cs`. Network connectivity is through the shared `common_app_network`
> (Phase 0, now part of the base `docker-compose.yml`, with no separate overlay).
> Left for the future: the typelib integration (Phase 1/2) and, if needed, moving validation to a
> separate gateway.

---

## Summary of changes
- **ta_vms:** the shared network (Phase 0; the bus and the state store stay its own); the
  `SquareIntegrationClient` typelib + changes in `VmsCfg`/`VmsFs` (gated by the env variable
  `SQUARE_INTEGRATION_SINK_APP_ID`) (Phase 1, partly); nothing in the heavy core.
- **Square:** read-only protection of the property panel for integration objects, a REST wrapper
  over `GetListByType`, a new "Monitor" tab (Phase 2, partly — see below).
- **Shared:** the new `SquareIntegrationClient` typelib (`ISquareIntegration` + no-op).

## Verification (per phase)
- **Phase 0:** `multitenant_admin` creates `common_app_network`; ta_vms joins the network by the
  same name (no overlay, `external: true`) — check app-id resolution between the stacks (Dapr
  service invocation `vmscfg` → `integrationhost`). ta_vms's bus and state store must not look at
  other projects' containers: `orderpubsub` is its own `rabbitmq`, `statestore` its own PostgreSQL.
  **ta_vms still does not start without Square/multitenant_admin up**, but now for exactly one
  reason — the network is declared `external` and is created by their stack (see CLAUDE.md).
- **Phase 1:** turn the integration on by env → the root `vmscfg` and the cameras appear in
  Square's tree; change a camera's configuration in ta_vms → the delta arrives; the
  `start_recording` command from Square → recording turns on (the status came back by uid);
  `resync` → a full re-push; delete the root in Square → the children disappear and the
  integration is broken off; per-object disable works.
- **Phase 2:** live (WebRTC) and archive (HLS) play in Square's UI through `web_srv`.
- **Phase 3:** a direct request to `web_srv` from outside without a token is refused; with a valid
  Keycloak token it passes.

## Open questions for the implementation stage
1. **Cross-stack Dapr service invocation:** confirm that, on a shared network, Dapr's default mDNS
   resolver finds the app ids of both stacks (otherwise configure a shared name-resolution
   component). The two sides' buses stay independent and know nothing of each other.
2. What does `video_srv` (C++) publish through `dapr_publisher.publish_json` — alarm and clip
   topics? (`grep -rn publish_json video_srv`) — does the typelib need them for the
   `last_alarm_ts` property?
3. ~~typelib: clean interfaces (SOLID, testability, no-op for standalone); the set of
   `ISquareIntegration` methods; where it lives (submodule vs NuGet)~~ — done (the
   `SquareIntegrationClient` submodule). Left: the schema of a self-describing property / command
   descriptor for VmsCfg.
4. The final reverse proxy for Phases 2–3 (nginx + oauth2-proxy vs Square's existing gateway).
5. The exact names of the integration's env flags (e.g. `SQUARE_INTEGRATION_SINK_APP_ID`) + adding
   them to every required place (`.env`, `../ta_install/.env`, the launch.json files) per CLAUDE.md.
