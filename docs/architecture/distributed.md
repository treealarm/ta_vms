# Scaling classes, the core-and-nodes layout, and infrastructure of its own

> Moved out of `CLAUDE.md`. These are the full statements; the rules themselves live there.

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
- `VmsMap` — the map's tree, states, events, rights and files, and the only owner of `ta_map`; also
  the adapter that turns camera configuration, retention episodes and source outages into nodes,
  states and journal events, and the `Watch` stream the web turns into SSE. **Replicable**: all its
  state is in the database, files included, and an alarm update recomputes the affected path's
  counters in its own transaction, so two replicas cannot each hold half the truth the way an
  in-memory aggregator would. It runs on the core, like `VmsCfg`.
- `roitrc` (the ROI re-compression service; the `ta_roienc` library it links lives in its own repository) — **sharded by node**, and this one is a correctness requirement
  rather than a capacity choice. It is handed absolute paths to clips on the node's own disk, so an
  instance elsewhere either finds nothing there or finds a *different* node's file at the same path
  and overwrites it. Which instance a node talks to therefore comes from that node's entry in
  VmsCfg's node registry (`roi_id`; the fifth part of a `MEDIA_SERVER_PAIRS` entry), resolved through VmsCfg — never from a global env
  var, which cannot express "the one next to me". A node that names none simply does not
  re-compress.

## Distributed layout: a core and nodes

A deployment is one **core** and any number of **nodes**, and one machine may be both.
- The core holds the shared infrastructure and everything system-wide: PostgreSQL, RabbitMQ, Dapr
  placement, Keycloak, `VmsCfg`, `VmsUtils` and the singletons.
- A node holds what is pinned to its own disk and subnet: its `vms`↔`vmsfs` pair, `vmsonvif`,
  `vmsonvif-actors` and `vmslogger`, each with app ids and an `OWN_SERVER_ID` of its own and an
  entry in VmsCfg's node registry. A node may run Linux (compose) or Windows (the installer).
- **Every machine runs a `web_srv`** (`web-vms-<node>` on a node): an operator opens any of them and
  sees the whole deployment as one system, logged in at the core's Keycloak, which therefore
  listens on the core's address. User administration (mt_admin) stays on the core.
- **MediaMTX runs on the core, and on a node unless it was joined without one** (`--no-mediamtx`,
  the installer's `NodeMediaMtx=0`). Which one a camera publishes to is configuration: a MediaMTX
  row in VmsCfg pinned to a node (`server_id`) or shared, and VmsCfg gives a camera its own node's
  first, so that its live video stays on that machine. Every MediaMTX of a deployment uses the
  core's accounts. Its API and WHEP listen on the machine's address, where VmsUtils and every web
  reach them.
- A standalone install — the Windows installer by default, `../ta_install` compose — is a core and
  a node on one machine, with fixed app ids.
- Over several machines, each is a core or a node, on Linux or Windows in any mix: on Linux
  `../ta_install/docker-compose.distributed.yml` through `scripts/distributed.sh core|join`,
  on Windows the installer with `Role=core|node` (`TAVMS_ROLE`), which runs
  `TaVmsHost core|join`. Both lay out the same files and read the same node package, so a
  core of either kind takes nodes of either.

Nothing may tie a service to "everything is on this machine": addresses and ports come only from
env, and the only place that fixes app ids and `OWN_SERVER_ID` for one machine is
`../ta_install/windows/config/services.json`, per role (and, on Linux,
`../ta_install/docker-compose.distributed.yml`); a node's app ids carry its name. Between
machines, sidecars cannot use the sqlite resolver (see "Infrastructure of its own" below); they use
HashiCorp Consul, and talk over mutual TLS with certificates from Dapr Sentry on the core, which
signs one only against a token for that app id (`../ta_install/scripts/distributed.sh`,
`../ta_install/windows/TaVmsHost/Distributed.cs`). A node takes from its core only the credentials
and ports of its PostgreSQL, RabbitMQ, MediaMTX and Keycloak and the WHEP session key (`core.env`
in the package), never the core's whole environment: a Windows core's holds Windows paths. Dapr's
other resolvers do not fit: `mdns` has no settings at all, so two installations on one LAN share
every app id, and `nameformat` returns a host name without a port, while on Windows every sidecar
has a port of its own. Consul and Sentry exist only in a deployment over several machines; one
machine runs neither.

A node's services find their partners through its entry in VmsCfg's node registry, which they read
from VmsCfg (`ListMediaServers`) — never from a deployment-wide app id variable, which on a node
would name the core's service.

**The node registry is VmsCfg's table, and joining is a request to the core's web.** A machine that
is to become a node asks `POST /api/deployment/join` for its package (`node-<name>.tar.gz`): VmsCfg
registers it and signs its Sentry tokens with the core's key (`TAVMS_DISTRIBUTED_DIR`). The core
learns the node there and then; nothing restarts. Joining has two stages. While the deployment is
being set up, enrollment is open and any machine that reaches the core's web gets in; once the
administrator closes it (Administration → Deployment), a new node needs an administrator's user name
and password, checked at Keycloak. A new package for a node already there (installed over, or at a
new address it then moves to) needs them even while enrollment is open: an added node shows in the
list, but a second package would let anyone become one unseen. Without Keycloak the join endpoint
takes nobody for an administrator. Enrollment starts open only on a new database; one VmsCfg ran on
before the switch existed starts closed. The administrator removes a node there as well; removal
does not yet revoke its tokens (`docs/future_plans/16-distributed-hardening.md`). `MEDIA_SERVER_PAIRS`
names only what this machine's own configuration fixes — the core's own node, and nodes joined with
a package before the registry existed — and VmsCfg writes those rows at every start, marked
configured: they cannot be removed in the web, since they would be back on the next start.

**The registry belongs to a tenant, not to a cluster.** In Kubernetes several companies may share a
cluster (`../multitenant_admin`: a tenant is a Keycloak realm, its databases and a namespace), each
with VmsCfg and webs of its own, so each has its own nodes, its own enrollment switch and its own
signing key — a Secret of its namespace there, a directory on a machine. multitenant_admin manages
who the users are; whether one of them may add servers is the realm's `admin` role, which is what
closed enrollment checks. Do not move the registry into multitenant_admin: it has no database and no
Dapr of its own, and a second copy of the nodes would have to be kept in step with this one. For
the same reason the package names each of the core's services by its own address (`POSTGRES_HOST`,
`RABBITMQ_HOST`, `VMS_MTX_HOST`, `KEYCLOAK_URL` in `core.env`) instead of leaving them to
`CORE_ADDRESS`: behind a cluster they are different addresses, and on a machine they are all the
core's.

**An app listens on the loopback only** (`ListenLocalhost`, `127.0.0.1` in `vms`): its own sidecar
is its one client, and it always runs beside it — the same host on Windows, the app's network
namespace in compose (`network_mode: service:<app>`). The one exception is `web_srv`, the browser's
way in. The sidecar's own API is its app's alone in the same way (`-dapr-listen-addresses
127.0.0.1` in both compose files and from TaVmsHost; daprd listens on every address otherwise, and
that API invokes any service and reads and writes the state store without asking who calls). What
crosses machines is the sidecar's internal gRPC port, never an app port or the API, so do not open
either to the network to make a call between machines work.

**Naming rule:** a service that cannot be replicated carries `Singleton` in its project name and Dapr
app-id. A reader must be able to tell the scaling class from the name alone, without reading
`Program.cs` to discover a background job hiding inside.

## Infrastructure of its own

ta_vms joins no other project's docker network and keeps no data in another project's container.
The dev compose and `../ta_install` compose each run in compose's own default network; the Windows
installer has no Docker at all.

- **The state store is its own PostgreSQL** (database `POSTGRES_STATE_DB`, component
  `init_files/dapr_components/statestore.yaml`), **the pub/sub its own `rabbitmq`**
  (`init_files/dapr_components/orderpubsub.yaml`; the component name `orderpubsub` is historical).
  Another project's `redis` belongs in neither: it is declared without a volume, and when databases
  are stamped out per tenant, a shared Redis with `keyPrefix: "name"` would give every tenant the
  same `cam-perm-version` key. Separately about Redis: two different containers with the same alias
  `redis` in one network make Docker answer DNS queries with a random round-robin between them
  (verified empirically) — so keeping our own Redis beside another one was impossible, and that is
  one of the reasons the broker became RabbitMQ (the second: Redis does not install natively on
  Windows, and the standalone installer needs it there).
- **App ids resolve over mDNS in Docker**; the single-machine Windows install uses Dapr's `sqlite`
  resolver with `DAPR_HOST_IP=127.0.0.1` instead, because mDNS announced the sidecars on the
  machine's LAN address — calls broke when that address changed or went away, and two installs on
  one LAN mixed their identical app ids.
