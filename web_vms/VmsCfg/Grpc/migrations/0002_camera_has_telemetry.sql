-- Whether a camera really has a motor behind the ONVIF PTZ service it advertises.
-- Defaults to false: a deployment must name its PTZ cameras explicitly, because the device's own
-- answer is what this column exists to stop trusting. See CameraSetup.has_telemetry in
-- proto/vms_cfg.proto.
ALTER TABLE camera_setup ADD COLUMN IF NOT EXISTS has_telemetry boolean NOT NULL DEFAULT false;
