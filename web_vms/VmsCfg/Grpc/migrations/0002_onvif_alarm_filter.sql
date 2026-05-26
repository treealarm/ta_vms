CREATE TABLE onvif_alarm_filter (
  id              uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
  camera_id       text        NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  filter_text     text        NOT NULL,
  timeout_seconds integer     NOT NULL DEFAULT 30,
  created_at      timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX idx_onvif_alarm_filter_camera_id ON onvif_alarm_filter (camera_id);
