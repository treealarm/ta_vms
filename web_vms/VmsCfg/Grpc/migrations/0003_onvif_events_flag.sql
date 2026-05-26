ALTER TABLE camera_setup
  ADD COLUMN IF NOT EXISTS onvif_events_enabled boolean NOT NULL DEFAULT false;
