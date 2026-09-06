-- Which cameras follow objects on their own, and what they follow.
--
-- Self-guidance is a property of the camera, not of whoever happens to be watching it: a camera
-- with a row here tracks whether or not an operator has the monitor open, and it goes on doing so
-- across a restart of the tracking service. An operator can still take over from the monitor and
-- name a particular object, but that override lives only in memory and expires with the object.
--
-- No row at all means switched off. That is not distinguished from a row with enabled = false, and
-- deliberately: there is nothing a deployment would do differently between the two.
--
-- See PtzTrackingPolicy in proto/vms_cfg.proto.
CREATE TABLE IF NOT EXISTS ptz_tracking_policy (
  camera_id text    PRIMARY KEY REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  enabled   boolean NOT NULL DEFAULT false,
  -- Object kinds in priority order, comma separated ("person,vehicle"). Empty means any object
  -- will do. Same words as the wire ("person", "face", "vehicle", ...), the way
  -- analytics_watch.classes already stores them.
  kinds     text    NOT NULL DEFAULT '',
  -- Which producers of boxes to believe: "cv" (the detector) and/or "onvif" (the camera's own
  -- metadata). Both by default, because a camera that has one of them configured should not have
  -- to name it here as well.
  sources   text    NOT NULL DEFAULT 'cv,onvif'
);
