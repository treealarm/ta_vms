# Analytics in a deployment over several machines

Raised 2026-10-23, while building the core/node split (CLAUDE.md, "Distributed layout"). Open: the
choice between the two options below is the user's.

## What there is

- **`analytics-worker`** (video_a) does the heavy part: decoding and OpenVINO inference. There can
  already be several, listed in `DAPR_ANALYTICS_WORKER_APP_IDS`.
- **`VmsAnalyticsSingleton`** is the control plane:
  - it decides which worker watches which camera and holds one `StreamDetections` per worker;
  - it persists events and crops;
  - it keeps the re-identification gallery, event grouping and debounce.

## The problem

A camera's worker is chosen by rendezvous hash over every worker in the deployment
(`AnalyticsWorkerRegistry.SelectWorker`); the camera's node plays no part in it. Since `vms`
publishes the camera's stream to the worker's listen URL (`WatchReconciler`, `ListenRtspUrl`), a
camera on node B can land on a worker on node A, and its video — megabits per second per camera —
then crosses the network instead of staying on the node that already has it.

So a worker belongs on every node that has cameras: at least one per machine, as many as its
hardware takes, in principle one per stream.

## Options

**A. Workers per node, one control plane, assignment aware of nodes** (recommended).
- A worker belongs to a node. The mapping is declared next to the node's pair: a sixth part of its
  `MEDIA_SERVER_PAIRS` entry, or a node → workers list; to be settled when this is built.
- A camera goes only to the workers of its own node, rendezvous among them.
- Video never leaves the node; only detections (kilobytes) reach the core.
- Re-identification and event grouping keep working across the whole deployment.
- No renaming.

**B. A control plane per node.**
- Every node runs its analytics on its own.
- Re-identification and event grouping stop at the node's boundary.
- The service is no longer a singleton, so its name and app id must change (CLAUDE.md naming rule),
  which is a migration for every installation (CLAUDE.md, "Source names and runtime names differ
  on purpose").

CLAUDE.md already names the workers as the scaling axis of analytics and warns that sharding the
control plane costs cross-camera re-identification; option A keeps both.

## Also to check when building it

- Crops: written by `VmsAnalyticsSingleton` to the camera's storage (`CameraStorageResolver`). With
  the storage on the node's disk and the control plane on the core, the path has to be reached
  through the node's VmsFs, not the core's file system.
- `docker-compose.distributed.yml` (ta_install) runs no analytics worker yet; the node profile gets
  one per node once assignment knows about nodes.

## When one control plane is not enough

Detections do not go through RabbitMQ: each worker streams them to the control plane over gRPC
(`StreamDetections`, through the two sidecars). RabbitMQ carries only configuration-change signals.
Still, every detection of every node ends in one process, and nothing has measured where that stops
scaling. What is in a message (video_a `analytics.proto`, `DetectionEvent`):
- the embedding (256 floats, about 1 KB for the person re-id model) on **every** frame of a live
  track, although it is recomputed only at track start and every `ANALYTICS_REID_EMBED_INTERVAL_SEC`;
- a crop JPEG on every frame for faces and licence plates.

Cheap reductions first, on the worker's side: the embedding only when `embedding_is_fresh`, crops
only for frames that can become events. Then, if one process is still the limit, Dapr's tool for
stateful work spread over replicas is actors: per-camera state (debounce, sessions, persisting)
in an actor per camera, which placement spreads over the replicas and moves when one goes. Replicas
of one app id share only stateless work (service invocation balancing, competing pub/sub
consumers), which is why the control plane is a singleton today. What an actor per camera does not
cover is the cross-camera re-identification gallery: it would move into shared storage (e.g.
pgvector in PostgreSQL) for the replicas to query.
