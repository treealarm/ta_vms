-- "Available for recording" is orthogonal to web_rtc: watching a stream and archiving it are
-- separate decisions, and until now there was no way to say "this one is for viewing only".
-- A profile of "off" did not express it, because an open recording schedule window forces
-- continuous recording on every stream of the camera regardless of the profile.
--
-- Default true so every existing stream stays recordable. The flag gates the *effective* mode
-- only; the configured profile is deliberately left in place, so clearing the flag and setting it
-- again resumes recording with the profile the operator had chosen.
ALTER TABLE camera_stream
  ADD COLUMN recordable boolean NOT NULL DEFAULT true;
