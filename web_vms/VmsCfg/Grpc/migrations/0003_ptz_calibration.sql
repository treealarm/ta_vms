CREATE TABLE IF NOT EXISTS ptz_calibration (
  camera_id    text PRIMARY KEY REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  pan_dx       real NOT NULL,
  pan_dy       real NOT NULL,
  tilt_dx      real NOT NULL,
  tilt_dy      real NOT NULL,
  confidence   real NOT NULL DEFAULT 0,
  calibrated_at timestamptz NOT NULL DEFAULT now(),
  method       text NOT NULL DEFAULT 'auto'
);
