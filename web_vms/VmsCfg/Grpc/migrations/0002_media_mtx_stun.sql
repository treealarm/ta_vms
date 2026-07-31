-- Configurable STUN server for browser WebRTC (WHEP) playback.
-- Empty string means "no STUN at all" — LAN-only host candidates. Existing rows are
-- backfilled with the previously hardcoded public STUN so behavior does not change.
ALTER TABLE media_mtx_server ADD COLUMN stun_url text NOT NULL DEFAULT '';

UPDATE media_mtx_server SET stun_url = 'stun:stun.l.google.com:19302';
