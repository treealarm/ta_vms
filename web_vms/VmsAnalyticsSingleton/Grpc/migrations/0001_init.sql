CREATE TABLE detections (
  id           bigserial   PRIMARY KEY,
  camera_id    text        NOT NULL,
  track_id     bigint      NOT NULL,
  kind         text        NOT NULL,
  confidence   real        NOT NULL,
  detected_at  timestamptz NOT NULL,
  -- Crop stored on a shared storage volume: storage_id + path relative to that storage's root.
  crop_path    text        NULL,
  storage_id   text        NULL,
  plate_text   text        NULL,
  -- event_id = the group this detection's episode belongs to; event_uuid = the specific episode
  -- (retention_events in ta_fs); object_id = the re-identification label of the physical object.
  event_id     text        NULL,
  event_uuid   uuid        NULL,
  object_id    text        NULL
);

CREATE INDEX idx_detections_camera_ts ON detections (camera_id, detected_at DESC);
CREATE INDEX idx_detections_plate_text ON detections (plate_text) WHERE plate_text IS NOT NULL;
CREATE INDEX idx_detections_event ON detections (event_id);
CREATE INDEX idx_detections_event_uuid ON detections (event_uuid);
CREATE INDEX idx_detections_object_id ON detections (object_id);

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
