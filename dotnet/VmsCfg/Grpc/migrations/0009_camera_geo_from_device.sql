-- Take the camera's place on the map from the device's own ONVIF position instead of the
-- administrator's placement. Off by default: a hand-placed camera, calibrated or aimed by survey,
-- must not drift because the device answers with a coordinate of its own. See
-- CameraSetup.geo_from_device in proto/vms_cfg.proto.
ALTER TABLE camera_setup
  ADD COLUMN IF NOT EXISTS geo_from_device boolean NOT NULL DEFAULT false;
