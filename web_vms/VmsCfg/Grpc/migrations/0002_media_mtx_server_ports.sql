ALTER TABLE media_mtx_server
  ADD COLUMN host        text    NOT NULL DEFAULT '',
  ADD COLUMN webrtc_host text    NOT NULL DEFAULT '',
  ADD COLUMN rtsp_port   integer NOT NULL DEFAULT 8554,
  ADD COLUMN webrtc_port integer NOT NULL DEFAULT 8889,
  ADD COLUMN api_port    integer NOT NULL DEFAULT 9997;

-- Migrate data from old URL fields where they were filled in
UPDATE media_mtx_server
  SET host = regexp_replace(rtsp_url, '^rtsp://([^:/]+).*', '\1')
  WHERE rtsp_url ~ '^rtsp://';

UPDATE media_mtx_server
  SET rtsp_port = CAST(regexp_replace(rtsp_url, '^rtsp://[^:]+:(\d+).*', '\1') AS integer)
  WHERE rtsp_url ~ '^rtsp://[^:]+:\d+';

UPDATE media_mtx_server
  SET webrtc_port = CAST(regexp_replace(webrtc_url, '^https?://[^:]+:(\d+).*', '\1') AS integer)
  WHERE webrtc_url ~ '^https?://[^:]+:\d+';

UPDATE media_mtx_server
  SET api_port = CAST(regexp_replace(api_url, '^https?://[^:]+:(\d+).*', '\1') AS integer)
  WHERE api_url ~ '^https?://[^:]+:\d+';

ALTER TABLE media_mtx_server
  DROP COLUMN rtsp_url,
  DROP COLUMN webrtc_url,
  DROP COLUMN api_url;
