ALTER TABLE clips
  ADD COLUMN IF NOT EXISTS retained BOOLEAN NOT NULL DEFAULT TRUE,
  ADD COLUMN IF NOT EXISTS retained_at TIMESTAMPTZ;

UPDATE clips SET retained = TRUE WHERE retained IS DISTINCT FROM TRUE;

CREATE INDEX IF NOT EXISTS idx_clips_camera_start_retained ON clips (camera_id, start_ts DESC) WHERE retained = TRUE;
CREATE INDEX IF NOT EXISTS idx_clips_retained_cleanup ON clips (retained, start_ts ASC);

CREATE TABLE IF NOT EXISTS retention_events (
  event_id TEXT PRIMARY KEY,
  camera_id TEXT NOT NULL,
  alarm_start_ts TIMESTAMPTZ NOT NULL,
  alarm_end_ts TIMESTAMPTZ,
  retain_from_ts TIMESTAMPTZ NOT NULL,
  retain_until_ts TIMESTAMPTZ NOT NULL,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_retention_events_camera_window ON retention_events (camera_id, retain_until_ts DESC, retain_from_ts ASC);

CREATE TABLE IF NOT EXISTS retention_event_clips (
  event_id TEXT NOT NULL REFERENCES retention_events (event_id) ON DELETE CASCADE,
  clip_uid UUID NOT NULL REFERENCES clips (clip_uid) ON DELETE CASCADE,
  role TEXT NOT NULL DEFAULT '',
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (event_id, clip_uid)
);

CREATE INDEX IF NOT EXISTS idx_retention_event_clips_clip ON retention_event_clips (clip_uid);
