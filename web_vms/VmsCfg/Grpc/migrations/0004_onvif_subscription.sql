CREATE TABLE onvif_event_subscription (
  camera_id   text PRIMARY KEY REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  created_at  timestamptz NOT NULL DEFAULT now()
);

INSERT INTO onvif_event_subscription (camera_id)
SELECT camera_id FROM camera_setup WHERE onvif_events_enabled = true
ON CONFLICT DO NOTHING;

ALTER TABLE camera_setup DROP COLUMN IF EXISTS onvif_events_enabled;
