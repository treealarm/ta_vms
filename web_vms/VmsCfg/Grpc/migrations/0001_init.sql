CREATE TABLE media_mtx_server (
  id          uuid    PRIMARY KEY DEFAULT gen_random_uuid(),
  name        text    NOT NULL,
  host        text    NOT NULL DEFAULT '',
  webrtc_host text    NOT NULL DEFAULT '',
  rtsp_port   integer NOT NULL DEFAULT 8554,
  webrtc_port integer NOT NULL DEFAULT 8889,
  api_port    integer NOT NULL DEFAULT 9997,
  -- Configurable STUN server for browser WebRTC (WHEP) playback.
  -- Empty string means "no STUN at all" — LAN-only host candidates.
  stun_url    text    NOT NULL DEFAULT ''
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

-- Recording schedules: named sets of time windows assigned to cameras. While a window is open the
-- camera records continuously, overriding both the stream's base profile and any manual/alarm
-- override. Nothing here is written at runtime — the active/inactive state is always recomputed by
-- VmsCfg from the wall clock, so it survives any restart.
CREATE TABLE recording_schedule (
  id              uuid    PRIMARY KEY DEFAULT gen_random_uuid(),
  name            text    NOT NULL UNIQUE,
  timezone        text    NOT NULL DEFAULT 'UTC',
  -- Seconds to switch recording on ahead of a window start, absorbing the keyframe wait and the
  -- notification round-trip so recording is already running when the window actually begins.
  lead_in_seconds integer NOT NULL DEFAULT 5
);

-- Seconds are counted from local midnight in the schedule's timezone, so 0..86400. end_second is
-- exclusive, and end_second <= start_second means the window wraps past midnight. Windows are
-- additive: a camera is recording if any window of any assigned schedule is open.
CREATE TABLE recording_schedule_window (
  id           uuid     PRIMARY KEY DEFAULT gen_random_uuid(),
  schedule_id  uuid     NOT NULL REFERENCES recording_schedule(id) ON DELETE CASCADE,
  kind         text     NOT NULL,  -- 'weekly' | 'date'
  day_of_week  smallint NULL,      -- 0=Sunday..6=Saturday, NULL for 'date'
  date_value   date     NULL,      -- NULL for 'weekly'
  start_second integer  NOT NULL,
  end_second   integer  NOT NULL
);

CREATE INDEX idx_schedule_window_schedule ON recording_schedule_window (schedule_id);

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

CREATE TABLE camera_schedule (
  camera_id   text NOT NULL REFERENCES camera_setup(camera_id)     ON DELETE CASCADE,
  schedule_id uuid NOT NULL REFERENCES recording_schedule(id)      ON DELETE CASCADE,
  PRIMARY KEY (camera_id, schedule_id)
);

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

-- Per-user camera access. username is a Keycloak preferred_username — the same identity string
-- monitor.owner already stores. There is deliberately no foreign key to a user table: Keycloak owns
-- identities, VmsCfg only labels grants with them, and a row naming a user Keycloak no longer has
-- is inert.
CREATE TABLE user_camera_permission (
  username     text    NOT NULL,
  camera_id    text    NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  view_live    boolean NOT NULL DEFAULT false,
  view_archive boolean NOT NULL DEFAULT false,
  ptz          boolean NOT NULL DEFAULT false,
  control      boolean NOT NULL DEFAULT false,
  export       boolean NOT NULL DEFAULT false,
  PRIMARY KEY (username, camera_id)
);

CREATE INDEX idx_user_camera_permission_username ON user_camera_permission (username);

-- Blanket grant covering every camera, including ones added later. The effective permission on a
-- camera is the OR of this row and the matching user_camera_permission row, so "live on everything
-- plus PTZ on the gate camera" needs no per-camera copies of the blanket flags.
CREATE TABLE user_all_cameras_permission (
  username     text    PRIMARY KEY,
  view_live    boolean NOT NULL DEFAULT false,
  view_archive boolean NOT NULL DEFAULT false,
  ptz          boolean NOT NULL DEFAULT false,
  control      boolean NOT NULL DEFAULT false,
  export       boolean NOT NULL DEFAULT false
);

-- Single-row switch for the whole ACL. 'open' = every authenticated user sees every camera,
-- 'enforced' = the tables above decide. A fresh install has nobody to lock out, so it starts secure.
CREATE TABLE camera_acl_setting (
  id   boolean PRIMARY KEY DEFAULT true CHECK (id),
  mode text    NOT NULL
);

INSERT INTO camera_acl_setting (mode) VALUES ('enforced');
