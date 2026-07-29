CREATE TABLE media_mtx_server (
  id          uuid    PRIMARY KEY DEFAULT gen_random_uuid(),
  name        text    NOT NULL,
  host        text    NOT NULL DEFAULT '',
  webrtc_host text    NOT NULL DEFAULT '',
  rtsp_port   integer NOT NULL DEFAULT 8554,
  webrtc_port integer NOT NULL DEFAULT 8889,
  api_port    integer NOT NULL DEFAULT 9997
);

CREATE TABLE camera_setup (
  camera_id           text    PRIMARY KEY,
  server_id           text    NOT NULL,
  cred_user           text    NULL,
  cred_pass           text    NULL,
  onvif_ip            text    NULL,
  onvif_port          integer NULL,
  media_mtx_server_id uuid    NULL REFERENCES media_mtx_server(id) ON DELETE SET NULL,
  disabled            boolean NULL,
  name                text    NOT NULL DEFAULT '',
  onvif_xaddr         text    NULL
);

CREATE TABLE recording_profile (
  id                  uuid    PRIMARY KEY DEFAULT gen_random_uuid(),
  name                text    NOT NULL UNIQUE,
  mode                text    NOT NULL DEFAULT 'continuous',
  pre_record_seconds  integer NOT NULL DEFAULT 0,
  post_record_seconds integer NOT NULL DEFAULT 0,
  segment_seconds     integer NOT NULL DEFAULT 60,
  is_override_preset  boolean NOT NULL DEFAULT false,
  is_builtin          boolean NOT NULL DEFAULT false
);

-- Well-known UUIDs so default profiles survive re-deploys
INSERT INTO recording_profile (id, name, mode, pre_record_seconds, post_record_seconds, segment_seconds, is_override_preset, is_builtin) VALUES
  ('00000000-0000-0000-0000-000000000001', 'Continuous',    'continuous', 0, 0, 60, true, true),
  ('00000000-0000-0000-0000-000000000002', 'Pre-alarm',     'prealarm',  5, 5, 5,  true, true),
  ('00000000-0000-0000-0000-000000000003', 'Recording Off', 'off',       0, 0, 60, true, true);

CREATE TABLE camera_stream (
  camera_id             text        NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  stream_name           text        NOT NULL,
  profile_token         text        NOT NULL DEFAULT '',
  path                  text        NOT NULL DEFAULT '',
  web_rtc               boolean     NOT NULL DEFAULT false,
  recording_profile_id  uuid        NULL REFERENCES recording_profile(id) ON DELETE SET NULL,
  override_profile_id   uuid        NULL REFERENCES recording_profile(id) ON DELETE SET NULL,
  alarm_override_until  timestamptz NULL,
  disabled              boolean     NULL,
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

-- 'wall' monitors are the shared, admin-managed video walls. 'investigation' boards belong to the
-- operator who created them and are only ever listed back to that operator.
--
-- owner is a Keycloak preferred_username, deliberately allowed to be empty: KEYCLOAK_URL is
-- optional, and with authentication disabled there is no identity at all — everything then lands in
-- the shared '' bucket, which is the expected standalone behaviour.
CREATE TABLE monitor (
  id      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  name    text NOT NULL,
  layout  text NOT NULL DEFAULT '2x2',
  kind    text NOT NULL DEFAULT 'wall',
  owner   text NOT NULL DEFAULT '',
  -- Names only have to be unique within one owner's set: two operators may each keep their own
  -- "Investigation 1", and neither should block the other from creating it.
  CONSTRAINT monitor_name_kind_owner_key UNIQUE (name, kind, owner)
);

CREATE INDEX idx_monitor_kind_owner ON monitor (kind, owner);

-- Ordered pool of cameras assigned to a monitor (more cameras can exist than layout slots)
CREATE TABLE monitor_camera (
  monitor_id  uuid NOT NULL REFERENCES monitor(id) ON DELETE CASCADE,
  position    int  NOT NULL,
  camera_id   text NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  PRIMARY KEY (monitor_id, position)
);

CREATE INDEX idx_monitor_camera_mid ON monitor_camera (monitor_id, position ASC);

-- Which camera is currently active in each layout slot (persisted)
CREATE TABLE monitor_slot (
  monitor_id  uuid NOT NULL REFERENCES monitor(id) ON DELETE CASCADE,
  slot_index  int  NOT NULL,
  camera_id   text NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  PRIMARY KEY (monitor_id, slot_index)
);

CREATE TABLE onvif_alarm_filter (
  id              uuid    PRIMARY KEY DEFAULT gen_random_uuid(),
  camera_id       text    NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  filter_text     text    NOT NULL,
  timeout_seconds integer NOT NULL DEFAULT 30,
  text_filter     text    NOT NULL DEFAULT ''
);

CREATE INDEX idx_onvif_alarm_filter_camera_id ON onvif_alarm_filter (camera_id);

CREATE TABLE onvif_event_subscription (
  camera_id            text    PRIMARY KEY REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  subscribed           boolean NOT NULL DEFAULT true,
  poll_timeout_seconds integer NOT NULL DEFAULT 5,
  message_limit        integer NOT NULL DEFAULT 1024
);

CREATE TABLE camera_states (
  camera_id text    PRIMARY KEY REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  armed     boolean NOT NULL DEFAULT false
);

-- Per-camera video analytics watch. timeout_seconds is the retention timeout for events this watch
-- raises; its default matches onvif_alarm_filter.timeout_seconds so both event sources behave alike.
CREATE TABLE analytics_watch (
  camera_id          text    PRIMARY KEY REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  stream_name        text    NOT NULL,
  enabled            boolean NOT NULL DEFAULT false,
  classes            text    NOT NULL DEFAULT 'person',
  min_confidence     real    NOT NULL DEFAULT 0.5,
  sample_fps         integer NOT NULL DEFAULT 1,
  attach_debug_crops boolean NOT NULL DEFAULT false,
  timeout_seconds    integer NOT NULL DEFAULT 10
);
