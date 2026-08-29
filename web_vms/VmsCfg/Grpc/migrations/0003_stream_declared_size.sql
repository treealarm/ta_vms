-- Resolution as the camera declares it, copied from the ONVIF profile the stream is bound to
-- (camera_stream.profile_token). It is a seed for the monitor's adaptive stream choice, which
-- otherwise cannot rank streams it has never opened: not selected -> not opened -> no observed
-- parameters -> not selected.
--
-- Deliberately NOT the source of truth. The real resolution can change without anyone editing the
-- configuration — a different ONVIF profile, a different file — so an observed measurement always
-- wins over these. Zero means "not declared", which is the case for a stream given a plain RTSP
-- path rather than an ONVIF profile.
ALTER TABLE camera_stream
  ADD COLUMN declared_width  int NOT NULL DEFAULT 0,
  ADD COLUMN declared_height int NOT NULL DEFAULT 0;
