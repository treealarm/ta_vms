-- Automatic gap recovery is a property of the camera, not of a stream.
--
-- The card holds one archive, and which of the camera's streams goes into it is chosen in the
-- camera's own interface — ONVIF does not report it (GetRecordingJobs comes back empty on the
-- devices tested, and a recording only names its video source, not the encoder). So the setting
-- both switches the feature on and says which of our streams the recovered footage belongs to:
-- empty means off.
ALTER TABLE camera_setup ADD COLUMN IF NOT EXISTS edge_backfill_stream text NOT NULL DEFAULT '';

UPDATE camera_setup c
SET edge_backfill_stream = (
  SELECT cs.stream_name FROM camera_stream cs
  WHERE cs.camera_id = c.camera_id AND cs.edge_backfill
  ORDER BY cs.stream_name
  LIMIT 1)
WHERE EXISTS (
  SELECT 1 FROM camera_stream cs WHERE cs.camera_id = c.camera_id AND cs.edge_backfill);

ALTER TABLE camera_stream DROP COLUMN IF EXISTS edge_backfill;
