ALTER TABLE camera_state
  ADD COLUMN IF NOT EXISTS recording_mode text NOT NULL DEFAULT 'off';

UPDATE camera_state
SET recording_mode = CASE WHEN recording THEN 'continuous' ELSE 'off' END;

ALTER TABLE camera_setup
  ADD COLUMN IF NOT EXISTS pre_record_seconds integer NOT NULL DEFAULT 30,
  ADD COLUMN IF NOT EXISTS post_record_seconds integer NOT NULL DEFAULT 30,
  ADD COLUMN IF NOT EXISTS prealarm_segment_seconds integer NOT NULL DEFAULT 5;
