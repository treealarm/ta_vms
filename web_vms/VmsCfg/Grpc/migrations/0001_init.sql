CREATE TABLE IF NOT EXISTS media_server_node (
  server_id text PRIMARY KEY,
  status text NOT NULL DEFAULT 'online',
  last_seen timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS camera_setup (
  camera_id              text PRIMARY KEY,
  server_id              text NOT NULL REFERENCES media_server_node(server_id),
  cred_user              text NULL,
  cred_pass              text NULL,
  onvif_ip               text NULL,
  onvif_port             integer NULL,
  pre_record_seconds     integer NOT NULL DEFAULT 30,
  post_record_seconds    integer NOT NULL DEFAULT 30,
  prealarm_segment_seconds integer NOT NULL DEFAULT 5
);

CREATE TABLE IF NOT EXISTS camera_stream (
  camera_id     text NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  stream_name   text NOT NULL,
  profile_token text NOT NULL DEFAULT '',
  rtsp_url      text NOT NULL DEFAULT '',
  web_rtc       boolean NOT NULL DEFAULT false,
  recording_mode text NOT NULL DEFAULT 'off',
  PRIMARY KEY (camera_id, stream_name)
);

CREATE INDEX IF NOT EXISTS idx_camera_stream_camera_id
  ON camera_stream (camera_id);

CREATE TABLE IF NOT EXISTS storage (
  storage_id     text PRIMARY KEY,
  root_path      text NOT NULL UNIQUE,
  reserved_bytes bigint NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS camera_storage (
  camera_id   text NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  storage_id  text NOT NULL REFERENCES storage(storage_id) ON DELETE CASCADE,
  priority    integer NOT NULL DEFAULT 0,
  PRIMARY KEY (camera_id, storage_id)
);

CREATE INDEX IF NOT EXISTS idx_camera_storage_priority
  ON camera_storage (camera_id, priority ASC);
