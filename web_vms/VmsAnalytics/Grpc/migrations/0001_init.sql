CREATE TABLE detections (
  id           bigserial PRIMARY KEY,
  camera_id    text        NOT NULL,
  watch_id     uuid        NOT NULL,
  track_id     bigint      NOT NULL,
  kind         text        NOT NULL,
  confidence   real        NOT NULL,
  detected_at  timestamptz NOT NULL,
  crop_ref     text        NULL,
  plate_text   text        NULL
);
CREATE INDEX idx_detections_camera_ts ON detections (camera_id, detected_at DESC);
CREATE INDEX idx_detections_plate_text ON detections (plate_text) WHERE plate_text IS NOT NULL;
