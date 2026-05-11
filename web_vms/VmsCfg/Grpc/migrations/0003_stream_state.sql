ALTER TABLE camera_stream ADD COLUMN IF NOT EXISTS web_rtc boolean NOT NULL DEFAULT false;
ALTER TABLE camera_stream ADD COLUMN IF NOT EXISTS recording_mode text NOT NULL DEFAULT 'off';

-- Migrate state from camera_state to each camera's streams
UPDATE camera_stream cs
SET web_rtc        = st.web_rtc,
    recording_mode = st.recording_mode
FROM camera_state st
WHERE cs.camera_id = st.camera_id;

DROP TABLE IF EXISTS camera_state;
