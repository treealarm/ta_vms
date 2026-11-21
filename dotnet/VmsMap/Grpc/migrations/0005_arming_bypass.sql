-- An explicit `disarmed` may carry a deadline: the node is excluded from an armed zone until
-- `bypass_until`, then the sweep restores `restore_mode` (the own mode it had before the bypass).
-- The deadline is a modifier of the decision, not a second state: a row with `armed = true` (or a
-- device with any mode but `disarmed`) cannot carry one.
ALTER TABLE object_arming
  ADD COLUMN bypass_until timestamptz,
  ADD COLUMN restore_mode text,
  ADD CONSTRAINT object_arming_bypass_pair
    CHECK ((bypass_until IS NULL) = (restore_mode IS NULL)),
  ADD CONSTRAINT object_arming_bypass_restore
    CHECK (restore_mode IS NULL OR restore_mode IN ('inherit', 'armed')),
  ADD CONSTRAINT object_arming_bypass_disarmed
    CHECK (bypass_until IS NULL OR NOT armed);

ALTER TABLE device_object_settings
  ADD COLUMN bypass_until timestamptz,
  ADD COLUMN restore_mode text,
  ADD CONSTRAINT device_object_settings_bypass_pair
    CHECK ((bypass_until IS NULL) = (restore_mode IS NULL)),
  ADD CONSTRAINT device_object_settings_bypass_restore
    CHECK (restore_mode IS NULL OR restore_mode IN ('inherit', 'armed')),
  ADD CONSTRAINT device_object_settings_bypass_disarmed
    CHECK (bypass_until IS NULL OR arm_mode = 'disarmed');

-- The sweep asks only for rows that have a deadline, so the index is partial and small.
CREATE INDEX idx_object_arming_bypass_due
  ON object_arming (bypass_until) WHERE bypass_until IS NOT NULL;
CREATE INDEX idx_device_settings_bypass_due
  ON device_object_settings (bypass_until) WHERE bypass_until IS NOT NULL;
