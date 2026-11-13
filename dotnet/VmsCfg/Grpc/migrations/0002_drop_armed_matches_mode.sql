-- A camera's mode belongs to VmsMap now, and VmsCfg keeps only the effective flag the map writes
-- for it. The flag no longer has to agree with the legacy arm_mode column: that column stays in
-- this release as the migration source for the map's own rows and is dropped in the next one.
ALTER TABLE camera_states
  DROP CONSTRAINT camera_states_armed_matches_mode;
