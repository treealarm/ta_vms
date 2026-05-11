CREATE TABLE IF NOT EXISTS camera_stream (
  camera_id     text NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  stream_name   text NOT NULL,
  profile_token text NOT NULL DEFAULT '',
  rtsp_url      text NOT NULL DEFAULT '',
  PRIMARY KEY (camera_id, stream_name)
);

CREATE INDEX IF NOT EXISTS idx_camera_stream_camera_id
  ON camera_stream (camera_id);

-- Migrate existing single rtsp_url to a stream named "main"
INSERT INTO camera_stream (camera_id, stream_name, rtsp_url)
SELECT camera_id, 'main', rtsp_url
FROM camera_setup
WHERE rtsp_url IS NOT NULL AND rtsp_url != '';

ALTER TABLE camera_setup DROP COLUMN rtsp_url;
