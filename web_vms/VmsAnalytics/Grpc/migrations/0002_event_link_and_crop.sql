-- Links each detection to the retention event it triggered (retention_events.event_id in ta_fs;
-- the analytics-{guid} id is generated once and used for both) and moves crop storage to the
-- shared storage volumes: storage_id + path relative to that storage's root, like clips do.
ALTER TABLE detections ADD COLUMN event_id text NULL;
ALTER TABLE detections ADD COLUMN storage_id text NULL;
ALTER TABLE detections RENAME COLUMN crop_ref TO crop_path;

CREATE INDEX idx_detections_event ON detections (event_id);
