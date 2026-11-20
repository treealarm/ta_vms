-- The device-fault alarm. A part a successful manifest no longer lists is a fault an operator
-- resolves; it is a source of its own because its lifecycle is not a sensor condition's: it is
-- raised regardless of arming and alarm settings, never writes ALARM, and stays latched until an
-- operator resets it after the part reports again.
ALTER TABLE alarms DROP CONSTRAINT alarms_source_check;
ALTER TABLE alarms ADD CONSTRAINT alarms_source_check
  CHECK (source IN ('sensor', 'retention', 'subscription', 'fault'));

-- A fault is closed by a reset, not by a false-alarm/other close: the operator says the part is
-- back and the fault is over. The condition alarms keep their own reasons.
ALTER TABLE alarms DROP CONSTRAINT alarms_close_reason_check;
ALTER TABLE alarms ADD CONSTRAINT alarms_close_reason_check
  CHECK (close_reason IN
    ('returned_to_normal', 'acknowledged_after_normal', 'auto_returned_to_normal',
     'device_gone', 'false_alarm', 'other', 'reset'));
