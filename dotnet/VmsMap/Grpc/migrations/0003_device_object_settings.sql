-- Behaviour of a logical device object: who decides its arming and what its contact means. The map
-- owns it because the map is the only consumer — VmsOnvif and the routing gateway read the
-- effective camera flag, which the map derives and writes into VmsCfg.
--
-- A camera keeps its mode here and its effective flag in VmsCfg; a digital input keeps its mode and
-- its alarm rule (which contact state is alarming) here. A row is created on demand: no row means
-- the defaults — inherit, and no alarm.
CREATE TABLE device_object_settings (
  object_id     uuid PRIMARY KEY REFERENCES objects (id) ON DELETE CASCADE,
  arm_mode      text NOT NULL DEFAULT 'inherit'
                CHECK (arm_mode IN ('inherit', 'armed', 'disarmed')),
  trigger_on    text CHECK (trigger_on IN ('open', 'closed')),
  alarm_enabled boolean NOT NULL DEFAULT false,
  CHECK (NOT alarm_enabled OR trigger_on IS NOT NULL)
);
