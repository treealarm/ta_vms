-- Re-encoding finished clips with ROI is a property of the camera.
--
-- The boxes it needs come from analytics watching the camera, not one of its
-- encodings, so the switch cannot sensibly live on a stream. Off by default:
-- this rewrites recorded footage, and lossy-to-lossy is not something to opt
-- anyone into silently.
ALTER TABLE camera_setup ADD COLUMN IF NOT EXISTS roi_transcode boolean NOT NULL DEFAULT false;
