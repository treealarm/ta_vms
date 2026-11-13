-- A camera is armed the way a sensor is: an explicit mode, otherwise the zone it stands in,
-- otherwise not at all. `armed` stays the effective flag every other service reads, so the map
-- derives it for the cameras that inherit.
ALTER TABLE camera_states
  ADD COLUMN arm_mode text NOT NULL DEFAULT 'inherit';

ALTER TABLE camera_states
  ADD CONSTRAINT camera_states_arm_mode_check
  CHECK (arm_mode IN ('inherit', 'armed', 'disarmed'));

-- A flag that was true was the only way to say "armed" before the modes existed: it was decided,
-- not inherited, so it stays on guard.
UPDATE camera_states SET arm_mode = 'armed' WHERE armed;

-- An explicit mode and the flag agree; under 'inherit' the flag is the map's to derive.
ALTER TABLE camera_states
  ADD CONSTRAINT camera_states_armed_matches_mode
  CHECK (arm_mode = 'inherit' OR armed = (arm_mode = 'armed'));
