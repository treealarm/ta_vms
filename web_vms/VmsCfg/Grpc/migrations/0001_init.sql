CREATE TABLE media_server_node (
  server_id text        PRIMARY KEY,
  status    text        NOT NULL DEFAULT 'online',
  last_seen timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE camera_setup (
  camera_id  text    PRIMARY KEY,
  server_id  text    NOT NULL REFERENCES media_server_node(server_id),
  cred_user  text    NULL,
  cred_pass  text    NULL,
  onvif_ip   text    NULL,
  onvif_port integer NULL
);

CREATE TABLE recording_profile (
  id                  uuid    PRIMARY KEY DEFAULT gen_random_uuid(),
  name                text    NOT NULL UNIQUE,
  mode                text    NOT NULL DEFAULT 'continuous',
  pre_record_seconds  integer NOT NULL DEFAULT 0,
  post_record_seconds integer NOT NULL DEFAULT 0,
  segment_seconds     integer NOT NULL DEFAULT 60,
  is_override_preset  boolean NOT NULL DEFAULT false
);

-- Well-known UUIDs so default profiles survive re-deploys
INSERT INTO recording_profile (id, name, mode, pre_record_seconds, post_record_seconds, segment_seconds, is_override_preset) VALUES
  ('00000000-0000-0000-0000-000000000001', 'Continuous',    'continuous', 0,  0,  60, true),
  ('00000000-0000-0000-0000-000000000002', 'Pre-alarm',     'prealarm',  30, 30, 5,  true),
  ('00000000-0000-0000-0000-000000000003', 'Recording Off', 'off',       0,  0,  60, true);

CREATE TABLE camera_stream (
  camera_id            text    NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  stream_name          text    NOT NULL,
  profile_token        text    NOT NULL DEFAULT '',
  rtsp_url             text    NOT NULL DEFAULT '',
  web_rtc              boolean NOT NULL DEFAULT false,
  recording_profile_id uuid    NULL REFERENCES recording_profile(id) ON DELETE SET NULL,
  override_profile_id  uuid    NULL REFERENCES recording_profile(id) ON DELETE SET NULL,
  PRIMARY KEY (camera_id, stream_name)
);

CREATE INDEX idx_camera_stream_camera_id
  ON camera_stream (camera_id);

CREATE TABLE storage (
  storage_id     text   PRIMARY KEY,
  root_path      text   NOT NULL UNIQUE,
  reserved_bytes bigint NOT NULL DEFAULT 0
);

CREATE TABLE camera_storage (
  camera_id  text    NOT NULL REFERENCES camera_setup(camera_id)  ON DELETE CASCADE,
  storage_id text    NOT NULL REFERENCES storage(storage_id)       ON DELETE CASCADE,
  priority   integer NOT NULL DEFAULT 0,
  PRIMARY KEY (camera_id, storage_id)
);

CREATE INDEX idx_camera_storage_priority
  ON camera_storage (camera_id, priority ASC);
