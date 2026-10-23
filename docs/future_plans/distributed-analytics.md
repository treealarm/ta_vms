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
