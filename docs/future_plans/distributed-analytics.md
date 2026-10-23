# Analytics in a deployment over several machines

Raised 2026-10-23, while building the core/node split (CLAUDE.md, "Distributed layout").

**Direction (user, 2026-10-23):** cameras of different nodes do see the same people minutes apart,
and recognising them across cameras — where the function is switched on — must rest on the
database, not on a process's memory. Searching the archive for a person or an object matters even
more. With identity in the database, nothing is left that needs `VmsAnalyticsSingleton` to be one
for the whole system, so analytics can follow the nodes. Not decided yet: the order of the work
and whether the per-node split happens at all before the database part (below).

## What there is

- **`analytics-worker`** (video_a): decoding and OpenVINO inference. Several may run, listed in
  `DAPR_ANALYTICS_WORKER_APP_IDS`.
- **`VmsAnalyticsSingleton`**, one per deployment:
  - assigns cameras to workers (rendezvous hash over every worker, the camera's node playing no
    part) and holds a `StreamDetections` per worker;
  - debounces, persists detections and their crops (on the camera's storage, i.e. the node's disk)
    and the archive geometry;
  - takes ONVIF detections and events from VmsOnvif; streams live boxes and events to web_srv and
    to VmsPtzTrackingSingleton;
  - **live re-identification in memory** (`EphemeralReidGallery`, a time window, empty after a
    restart) and the event grouping that follows from it (`ObjectSessionService`);
  - **archive search** (`SearchSimilarDetections`) over the `detection_embeddings` table — by
    loading every vector of the requested period and computing cosines in C#.

Problems:
1. A camera on node B can be watched by a worker on node A, and its video then crosses the network.
2. Every detection of every node goes through one process.
3. Archive search is a full scan in the service's memory: it does not scale with months of archive
   from many cameras, distributed or not.
4. Identity across cameras lives in one process's memory and is lost on restart.

## The direction

**Identity and search in PostgreSQL, with pgvector.** One store of embeddings (the existing
`detection_embeddings`, as a `vector` column with an HNSW index) answers both questions with the
same nearest-neighbour query and a different time window:
- live: is this the object seen on any camera in the last minutes (then reuse its `object_id`)?
- archive: where else was this object over the period the operator asks for?

Only fresh embeddings query (track start and every `ANALYTICS_REID_EMBED_INTERVAL_SEC`), not every
frame. Re-identification stays a function to switch on, per camera or per deployment.

**Then analytics per node**, sharded like VmsOnvif: `VmsAnalytics` with `OWN_SERVER_ID`, app id
`vmsanalytics-<node>`, next to the node's workers.
- A camera goes only to the workers of its own node; its video never leaves the node.
- Crops and archive geometry are written to the node's own disk, by a process on that node.
- Load splits by node; there is no single process every detection passes through.
- All of them share the database on the core, so identity and search stay deployment-wide.

## Costs and open questions

- **pgvector in every PostgreSQL**: the Docker images move to one with the extension
  (`pgvector/pgvector`), and the Windows installer has to ship the extension for its pinned
  PostgreSQL (`../ta_install/windows/versions.json`).
- **The rename**: no longer a singleton, so `VmsAnalyticsSingleton` becomes `VmsAnalytics` and its
  app id changes — a migration for every installation (CLAUDE.md, "Source names and runtime names
  differ on purpose"): env, compose, `services.json`, and the old app id's RabbitMQ queues to
  delete. The database `ta_analytics` stays.
- **Callers route by the camera's node**: web_srv (live boxes and events, search),
  VmsPtzTrackingSingleton (live detections), VmsOnvif (pushes to its own node's instance).
- **Two nodes seeing one person at the same moment** may each mint an `object_id`; a rule to merge
  them later is needed.
- **Event grouping** (`ObjectSessionService`) is keyed by `object_id`; with identity in the
  database it can be decided per node, by whichever instance sees the object.
- `docker-compose.distributed.yml` (ta_install) runs no analytics yet; the node profile gets a
  worker and its `VmsAnalytics` once this is in place.

## Message size, for when it is measured

Each `DetectionEvent` (video_a `analytics.proto`) carries the embedding (256 floats, about 1 KB)
on every frame of a live track, although it changes only when `embedding_is_fresh`, and a crop JPEG
on every frame for faces and licence plates. Sending the embedding only when fresh and crops only
for frames that can become events would cut the stream from worker to analytics several times over,
whichever machine analytics runs on.
