CREATE TABLE media_mtx_server (
  id          uuid  PRIMARY KEY DEFAULT gen_random_uuid(),
  name        text  NOT NULL,
  rtsp_url    text  NOT NULL,
  webrtc_url  text  NOT NULL
);

CREATE TABLE media_server_node (
  server_id text        PRIMARY KEY,
  status    text        NOT NULL DEFAULT 'online',
  last_seen timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE camera_setup (
  camera_id           text    PRIMARY KEY,
  server_id           text    NOT NULL REFERENCES media_server_node(server_id),
  cred_user           text    NULL,
  cred_pass           text    NULL,
  onvif_ip            text    NULL,
  onvif_port          integer NULL,
  media_mtx_server_id uuid    NULL REFERENCES media_mtx_server(id) ON DELETE SET NULL
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
  camera_id             text        NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  stream_name           text        NOT NULL,
  profile_token         text        NOT NULL DEFAULT '',
  rtsp_url              text        NOT NULL DEFAULT '',
  web_rtc               boolean     NOT NULL DEFAULT false,
  recording_profile_id  uuid        NULL REFERENCES recording_profile(id) ON DELETE SET NULL,
  override_profile_id   uuid        NULL REFERENCES recording_profile(id) ON DELETE SET NULL,
  alarm_override_until  timestamptz NULL,
  PRIMARY KEY (camera_id, stream_name)
);

CREATE INDEX idx_camera_stream_camera_id
  ON camera_stream (camera_id);

CREATE INDEX idx_camera_stream_alarm_override
  ON camera_stream (alarm_override_until)
  WHERE alarm_override_until IS NOT NULL;

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

CREATE TABLE monitor (
  id      uuid  PRIMARY KEY DEFAULT gen_random_uuid(),
  name    text  NOT NULL UNIQUE,
  layout  text  NOT NULL DEFAULT '2x2'
);

-- Ordered pool of cameras assigned to a monitor (more cameras can exist than layout slots)
CREATE TABLE monitor_camera (
  monitor_id  uuid  NOT NULL REFERENCES monitor(id) ON DELETE CASCADE,
  position    int   NOT NULL,
  camera_id   text  NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  PRIMARY KEY (monitor_id, position)
);

CREATE INDEX idx_monitor_camera_mid ON monitor_camera (monitor_id, position ASC);

-- Which camera is currently active in each layout slot (persisted)
CREATE TABLE monitor_slot (
  monitor_id  uuid  NOT NULL REFERENCES monitor(id) ON DELETE CASCADE,
  slot_index  int   NOT NULL,
  camera_id   text  NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  PRIMARY KEY (monitor_id, slot_index)
);
