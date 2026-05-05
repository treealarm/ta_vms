CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TABLE IF NOT EXISTS clips (
  clip_uid uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  file_path text NOT NULL UNIQUE,
  camera_id text NOT NULL,
  start_ts timestamptz NOT NULL,
  end_ts timestamptz NULL,
  size_bytes bigint NOT NULL DEFAULT 0,
  retained boolean NOT NULL DEFAULT TRUE
);

CREATE INDEX IF NOT EXISTS clips_camera_time_idx
  ON clips (camera_id, start_ts, end_ts);

CREATE INDEX IF NOT EXISTS idx_clips_camera_start_retained
  ON clips (camera_id, start_ts DESC) WHERE retained = TRUE;

CREATE INDEX IF NOT EXISTS idx_clips_retained_cleanup
  ON clips (retained, start_ts ASC);

CREATE TABLE IF NOT EXISTS retention_events (
  event_id text PRIMARY KEY,
  camera_id text NOT NULL,
  alarm_start_ts timestamptz NOT NULL,
  alarm_end_ts timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_retention_events_camera_time
  ON retention_events (camera_id, alarm_start_ts DESC, alarm_end_ts);
