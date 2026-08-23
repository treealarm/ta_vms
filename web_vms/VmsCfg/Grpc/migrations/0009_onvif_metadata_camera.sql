-- Per-camera switch for the camera's own ONVIF object boxes.
--
-- Default false keeps today's behaviour: the boxes are accepted. Turning it on drops them at
-- ingestion, which is a decision about this deployment rather than about the device -- the
-- camera keeps sending either way.

ALTER TABLE camera_setup ADD COLUMN IF NOT EXISTS onvif_metadata_disabled boolean NOT NULL DEFAULT false;
