-- Opt-in automatic recovery of archive gaps from the camera's own storage.
--
-- Off by default and per stream, not global: a replay session is one of the very few RTSP sessions a
-- camera has, and taking one on a device that is also being watched live costs the operator a
-- picture. Turning it on is a decision about that camera, so it lives with the camera.
ALTER TABLE camera_stream ADD COLUMN IF NOT EXISTS edge_backfill boolean NOT NULL DEFAULT false;
