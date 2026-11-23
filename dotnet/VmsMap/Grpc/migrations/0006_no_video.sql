-- Video loss is not a network loss: a camera that is reachable but delivers no frames shows this
-- state beside CONNECTED/DISCONNECTED. It must not raise alarms or light up spaces — the red and
-- alarming state is DISCONNECTED alone.
INSERT INTO object_state_descriptions (state, state_descr, state_color, alarm) VALUES
  ('NO_VIDEO', 'No video', '#ffc107', false)
ON CONFLICT (state) DO NOTHING;

-- The presence sweep re-checks exactly the cameras whose video is gone.
CREATE INDEX IF NOT EXISTS idx_object_state_values_no_video
  ON object_state_values (object_id)
  WHERE state = 'NO_VIDEO';
