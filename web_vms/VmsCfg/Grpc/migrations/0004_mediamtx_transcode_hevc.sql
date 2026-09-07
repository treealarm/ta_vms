-- Whether cameras publishing to this MediaMTX get their HEVC re-encoded to H.264 for WebRTC.
-- Default false: H.265 passthrough is what a capable browser should get, and re-encoding is the
-- exception turned on where the viewers or the stream cannot do without it.
ALTER TABLE media_mtx_server
  ADD COLUMN IF NOT EXISTS transcode_hevc boolean NOT NULL DEFAULT false;
