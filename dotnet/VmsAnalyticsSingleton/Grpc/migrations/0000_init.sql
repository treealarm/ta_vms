-- The whole schema of ta_analytics, one baseline. There were no releases carrying data, so the
-- migrations that grew it one feature at a time are folded into this file and the database is
-- recreated instead of upgraded (docs/plans/square-merge/m2.4a-foundation.md).
--
-- camera_id and storage_id are UUIDs, the ids VmsCfg assigns. The crop a detection did not save is
-- NULL.

CREATE TABLE detections (
  id           bigserial   PRIMARY KEY,
  camera_id    uuid        NOT NULL,
  track_id     bigint      NOT NULL,
  kind         text        NOT NULL,
  confidence   real        NOT NULL,
  detected_at  timestamptz NOT NULL,
  -- Crop stored on a shared storage volume: storage_id + path relative to that storage's root.
  crop_path    text        NULL,
  storage_id   uuid        NULL,
  plate_text   text        NULL,
  -- event_id = the group this detection's episode belongs to; event_uuid = the specific episode
  -- (retention_events in ta_fs); object_id = the re-identification label of the physical object.
  event_id     text        NULL,
  event_uuid   uuid        NULL,
  object_id    text        NULL,
  -- When this detection was handed to VmsMap as `analytics-event`. NULL means "not yet" (or
  -- "given up on"): the row is itself the record a crash cannot lose, because it is written in
  -- the same transaction as the detection. The publisher's sweep picks these up; rows older than
  -- an hour are marked without being sent (docs/plans/square-merge/m2.4e-analytics-admin-demo.md).
  map_event_at timestamptz NULL
);

CREATE TABLE detection_embeddings (
  detection_id bigint      PRIMARY KEY REFERENCES detections(id) ON DELETE CASCADE,
  kind         text        NOT NULL,
  dim          integer     NOT NULL,
  embedding    bytea       NOT NULL,
  -- Which model produced each embedding (analytics.proto DetectionEvent.embedding_model). Vectors of
  -- different models are not comparable, even when they are the same length, so search and the
  -- re-id gallery compare a vector only with vectors of its own model. NULL marks the rows written
  -- before the worker named its model -- all of them came from the OSNet 512-d body model it shipped
  -- until then -- and the vectors of a worker that still does not.
  model        text        NULL
);

CREATE INDEX idx_detections_camera_ts ON detections (camera_id, detected_at DESC);
CREATE INDEX idx_detections_plate_text ON detections (plate_text) WHERE plate_text IS NOT NULL;
CREATE INDEX idx_detections_event ON detections (event_id);
CREATE INDEX idx_detections_event_uuid ON detections (event_uuid);
CREATE INDEX idx_detections_object_id ON detections (object_id);
-- The publisher's sweep asks only for what has not been handed to the map; a partial index keeps
-- it proportional to the unpublished backlog, not to the archive.
CREATE INDEX idx_detections_unpublished ON detections (id) WHERE map_event_at IS NULL;
CREATE INDEX idx_detection_embeddings_kind ON detection_embeddings (kind);
CREATE INDEX idx_detection_embeddings_kind_model ON detection_embeddings (kind, model);

-- Where object ids ("obj-1", "obj-2", ...) come from.
--
-- Not an in-memory counter seeded from MAX(object_id) at startup: that is only safe while exactly
-- one process ever hands them out, and a second one -- a replica, or simply a stale process nobody
-- noticed -- starts from the same seed and gives two different physical objects the same id.
-- object_id is written to detections and travels to retention_events, so the collision lands in
-- history and cannot be repaired afterwards.
--
-- INCREMENT BY 100 makes each nextval() hand out a whole block of numbers to one process, which
-- then issues them from memory: the database is touched once per hundred new objects instead of on
-- the hot path, and two processes can never be inside the same block. The cost is a gap in the
-- numbering after a restart, which is harmless -- these are labels, not a count of anything.
CREATE SEQUENCE detections_object_num_seq AS bigint INCREMENT BY 100 START WITH 1;
