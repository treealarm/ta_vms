CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TABLE IF NOT EXISTS clips (
  clip_uid uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  file_path text NOT NULL UNIQUE,
  camera_id text NOT NULL,
  start_ts timestamptz NOT NULL,
  end_ts timestamptz NULL,
  size_bytes bigint NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS clips_camera_time_idx
  ON clips (camera_id, start_ts, end_ts);
