CREATE TABLE IF NOT EXISTS recording_profile (
  id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  name                text NOT NULL UNIQUE,
  mode                text NOT NULL DEFAULT 'continuous',
  pre_record_seconds  integer NOT NULL DEFAULT 0,
  post_record_seconds integer NOT NULL DEFAULT 0,
  segment_seconds     integer NOT NULL DEFAULT 60
);

INSERT INTO recording_profile (id, name, mode, pre_record_seconds, post_record_seconds, segment_seconds) VALUES
  ('00000000-0000-0000-0000-000000000001', 'Continuous',  'continuous', 0,  0,  60),
  ('00000000-0000-0000-0000-000000000002', 'Pre-alarm',   'prealarm',  30, 30, 5)
ON CONFLICT (id) DO NOTHING;

ALTER TABLE camera_stream
  ADD COLUMN IF NOT EXISTS recording_profile_id uuid NULL REFERENCES recording_profile(id) ON DELETE SET NULL;

ALTER TABLE camera_stream
  DROP COLUMN IF EXISTS recording_mode;

ALTER TABLE camera_setup
  DROP COLUMN IF EXISTS pre_record_seconds,
  DROP COLUMN IF EXISTS post_record_seconds,
  DROP COLUMN IF EXISTS prealarm_segment_seconds;
