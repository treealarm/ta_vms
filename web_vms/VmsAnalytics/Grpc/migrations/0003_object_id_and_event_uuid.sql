-- Two-level event identity + re-identification link. `event_id` (added in 0002) is now the GROUP
-- label; `event_uuid` is the specific episode (one retention open->close cycle, owned by ta_fs and
-- returned by RetainEvent). `object_id` is the re-identification label of the physical object
-- across cameras/time (assigned in-memory by VmsAnalytics; persisted here only as a snapshot).
ALTER TABLE detections ADD COLUMN event_uuid uuid NULL;
ALTER TABLE detections ADD COLUMN object_id text NULL;

CREATE INDEX idx_detections_event_uuid ON detections (event_uuid);
CREATE INDEX idx_detections_object_id ON detections (object_id);
