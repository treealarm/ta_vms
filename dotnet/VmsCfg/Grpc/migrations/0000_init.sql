-- The whole schema of ta_cfg, one baseline. There were no releases carrying data, so the
-- migrations that grew it one feature at a time are folded into this file and the database is
-- recreated instead of upgraded (docs/plans/square-merge/m2.4a-foundation.md).
--
-- Camera and storage ids are UUIDs: a camera's human-readable name is camera_setup.name, and a
-- storage's is its root_path. Proto and JSON keep carrying them as strings -- the DB layer is the
-- only place that knows the type.

CREATE EXTENSION IF NOT EXISTS pgcrypto;

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
  stun_url    text    NOT NULL DEFAULT '',
  -- Which node this MediaMTX belongs to. video_srv pushes a camera's stream to the
  -- MediaMTX assigned to that camera, so an assignment that ignores the node sends video
  -- across whatever link separates them. NULL means "shared": the server answers for any
  -- node, which is what a single-box installation has.
  server_id   text    NULL,
  publish_user text   NOT NULL DEFAULT '',
  publish_pass text   NOT NULL DEFAULT '',
  read_user    text   NOT NULL DEFAULT '',
  read_pass    text   NOT NULL DEFAULT '',
  -- Whether cameras publishing here get their HEVC re-encoded to H.264 for WebRTC. Default
  -- false: H.265 passthrough is what a capable browser should get, and re-encoding is the
  -- exception turned on where the viewers or the stream cannot do without it.
  transcode_hevc boolean NOT NULL DEFAULT false
);

CREATE TABLE camera_setup (
  camera_id           uuid    PRIMARY KEY,
  server_id           text    NOT NULL,
  cred_user           text    NULL,
  cred_pass           text    NULL,
  onvif_ip            text    NULL,
  onvif_port          integer NULL,
  media_mtx_server_id uuid    NULL REFERENCES media_mtx_server(id) ON DELETE SET NULL,
  disabled            boolean NULL,
  name                text    NOT NULL DEFAULT '',
  onvif_xaddr         text    NULL,

  -- Automatic recovery of archive gaps from the camera's own storage, and which of our
  -- streams the recovered footage belongs to; empty means off. A property of the camera
  -- rather than of a stream: the card holds one archive, and ONVIF does not report which
  -- stream feeds it (GetRecordingJobs comes back empty on the devices tested, and a
  -- recording only names its video source, not the encoder).
  edge_backfill_stream    text             NOT NULL DEFAULT '',

  -- Re-encode this camera's finished clips with ROI. A property of the camera because the
  -- boxes come from analytics watching the camera, not one of its encodings. Off by
  -- default: this rewrites recorded footage, and lossy-to-lossy is not something to opt
  -- anyone into silently.
  roi_transcode           boolean          NOT NULL DEFAULT false,

  -- How long a clip stays untouched before the pass may re-encode it. Hours because that is
  -- the scale operators think in, fractional because the useful values are as often
  -- minutes. 0 does not mean "immediately": the pass still waits for the clip to close and
  -- for the boxes to be written, which is a correctness floor and lives in code.
  roi_delay_hours         double precision NOT NULL DEFAULT 0,

  -- The band of ROI coverage in which re-encoding is worth doing: below the floor the
  -- detector barely saw anything and crushing the clip takes its word for the whole thing;
  -- above the ceiling there is little background left to crush. Per camera, because what
  -- counts as "barely saw anything" on a corridor is an ordinary afternoon on a checkout
  -- counter. The defaults leave the band wide open, which is the fastest way to fill
  -- clips.roi_coverage and find out what the real distribution looks like.
  roi_min_coverage        double precision NOT NULL DEFAULT 0,
  roi_max_coverage        double precision NOT NULL DEFAULT 1,

  -- Ignore the object boxes this camera puts in its own RTSP metadata track. Not the same
  -- as switching analytics off on the device -- that is the ONVIF metadata configuration,
  -- and it has its own control. This one is ours: the camera keeps sending, and the
  -- documents are dropped before they are parsed.
  onvif_metadata_disabled boolean          NOT NULL DEFAULT false,

  -- Whether the camera really has a motor behind the ONVIF PTZ service it advertises. Defaults
  -- to false: a deployment must name its PTZ cameras explicitly, because the device's own answer
  -- is what this column exists to stop trusting. See CameraSetup.has_telemetry in
  -- proto/vms_cfg.proto.
  has_telemetry           boolean          NOT NULL DEFAULT false,

  -- Take the camera's place on the map from the device's own ONVIF position instead of the
  -- administrator's placement. Off by default: a hand-placed camera, calibrated or aimed by
  -- survey, must not drift because the device answers with a coordinate of its own.
  geo_from_device         boolean          NOT NULL DEFAULT false
);

CREATE INDEX idx_camera_setup_name ON camera_setup (name, camera_id);

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
  camera_id             uuid        NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  stream_name           text        NOT NULL,
  profile_token         text        NOT NULL DEFAULT '',
  path                  text        NOT NULL DEFAULT '',
  web_rtc               boolean     NOT NULL DEFAULT false,
  recording_profile_id  uuid        NULL REFERENCES recording_profile(id) ON DELETE SET NULL,
  override_profile_id   uuid        NULL REFERENCES recording_profile(id) ON DELETE SET NULL,
  alarm_override_until  timestamptz NULL,
  disabled              boolean     NULL,
  -- Viewing and recording are independent: false keeps the stream available for live playback
  -- while excluding it from schedules and recording overrides.
  recordable            boolean     NOT NULL DEFAULT true,
  -- Resolution declared by the bound ONVIF profile. Zero means unknown; observed stream
  -- measurements remain authoritative.
  declared_width        integer     NOT NULL DEFAULT 0,
  declared_height       integer     NOT NULL DEFAULT 0,
  PRIMARY KEY (camera_id, stream_name)
);

CREATE INDEX idx_camera_stream_camera_id
  ON camera_stream (camera_id);

CREATE INDEX idx_camera_stream_alarm_override
  ON camera_stream (alarm_override_until)
  WHERE alarm_override_until IS NOT NULL;

CREATE TABLE storage (
  storage_id     uuid    PRIMARY KEY,
  root_path      text    NOT NULL UNIQUE,
  reserved_bytes bigint  NOT NULL DEFAULT 0,
  -- Out of service without being removed. Deleting the row would take its camera
  -- assignments with it, so a disk pulled for maintenance and put back would mean
  -- reassigning every camera by hand. This keeps the row and the assignments and only stops
  -- new recordings from choosing it; clips already on it stay readable, because playback,
  -- export and cleanup all resolve their paths through the same row.
  disabled       boolean NOT NULL DEFAULT false
);

CREATE TABLE camera_storage (
  camera_id  uuid    NOT NULL REFERENCES camera_setup(camera_id)  ON DELETE CASCADE,
  storage_id uuid    NOT NULL REFERENCES storage(storage_id)       ON DELETE CASCADE,
  priority   integer NOT NULL DEFAULT 0,
  PRIMARY KEY (camera_id, storage_id)
);

CREATE INDEX idx_camera_storage_priority
  ON camera_storage (camera_id, priority ASC);

CREATE TABLE camera_schedule (
  camera_id   uuid NOT NULL REFERENCES camera_setup(camera_id)     ON DELETE CASCADE,
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
  camera_id   uuid NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  PRIMARY KEY (monitor_id, position)
);

CREATE INDEX idx_monitor_camera_mid ON monitor_camera (monitor_id, position ASC);

-- Which camera is currently active in each layout slot (persisted)
CREATE TABLE monitor_slot (
  monitor_id  uuid NOT NULL REFERENCES monitor(id) ON DELETE CASCADE,
  slot_index  int  NOT NULL,
  camera_id   uuid NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  PRIMARY KEY (monitor_id, slot_index)
);

-- Per-operator stream selection for each monitor slot. Empty stream_name means automatic
-- selection. owner may be empty when authentication is disabled.
CREATE TABLE monitor_slot_pref (
  owner       text NOT NULL,
  monitor_id  uuid NOT NULL REFERENCES monitor(id) ON DELETE CASCADE,
  slot_index  int  NOT NULL,
  stream_name text NOT NULL DEFAULT '',
  PRIMARY KEY (owner, monitor_id, slot_index)
);

CREATE INDEX idx_monitor_slot_pref_lookup
  ON monitor_slot_pref (owner, monitor_id);

CREATE TABLE onvif_alarm_filter (
  id              uuid    PRIMARY KEY DEFAULT gen_random_uuid(),
  camera_id       uuid    NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  filter_text     text    NOT NULL,
  timeout_seconds integer NOT NULL DEFAULT 30,
  text_filter     text    NOT NULL DEFAULT ''
);

CREATE INDEX idx_onvif_alarm_filter_camera_id ON onvif_alarm_filter (camera_id);

CREATE TABLE onvif_event_subscription (
  camera_id            uuid    PRIMARY KEY REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  subscribed           boolean NOT NULL DEFAULT true,
  poll_timeout_seconds integer NOT NULL DEFAULT 5,
  message_limit        integer NOT NULL DEFAULT 1024
);

CREATE TABLE camera_states (
  camera_id uuid    PRIMARY KEY REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  armed     boolean NOT NULL DEFAULT false
);

-- What the operator says about a camera's digital inputs and relays. A camera stays one row of
-- camera_setup; its parts are described here, keyed by the ONVIF token the device itself uses.
--
-- No row is not a missing configuration: it is the default (inherit the camera's arming, no alarm,
-- name from the token). The adapter never writes a row on discovery; only an administrator does.
-- Arming fields belong to an input: a relay is driven by a command, not watched for a contact, and
-- storing arming on one would be a configuration the product cannot apply.
CREATE TABLE device_settings (
  camera_id     uuid NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  kind          text NOT NULL CHECK (kind IN ('digital_input','relay')),
  token         text NOT NULL,
  name          text NOT NULL DEFAULT '',
  arm_mode      text NOT NULL DEFAULT 'inherit' CHECK (arm_mode IN ('inherit','armed','disarmed')),
  trigger_on    text CHECK (trigger_on IN ('open','closed')),
  alarm_enabled boolean NOT NULL DEFAULT false,
  PRIMARY KEY (camera_id, kind, token),
  CHECK (kind = 'digital_input' OR (arm_mode = 'inherit' AND trigger_on IS NULL AND NOT alarm_enabled)),
  CHECK (NOT alarm_enabled OR trigger_on IS NOT NULL)
);

-- The definitions an alarm is raised under. A definition names its severity and what its
-- acknowledgement policy is; the alarm rows themselves live in VmsMap's database, which keeps this
-- id as a plain column (no foreign key across the two databases). There is no CRUD in M2.4: the
-- three seeds below are the whole set, and a raise names one of them.
CREATE TABLE alarm_definitions (
  id         uuid NOT NULL PRIMARY KEY,
  name       text NOT NULL UNIQUE,
  severity   text NOT NULL DEFAULT 'warning' CHECK (severity IN ('warning', 'critical')),
  ack_policy text NOT NULL DEFAULT 'required'  CHECK (ack_policy IN ('required', 'auto_on_normal'))
);

-- Fixed ids: a raise travels the bus as a UUID and the alarm row keeps it forever, so it must not
-- depend on what the migration happened to generate. A retention episode closes itself when it
-- ends; a sensor's contact and a subscription are the operator's to acknowledge.
INSERT INTO alarm_definitions (id, name, severity, ack_policy) VALUES
  ('00000000-0000-0000-0000-00000000a101', 'sensor',       'warning', 'required'),
  ('00000000-0000-0000-0000-00000000a102', 'retention',    'warning', 'auto_on_normal'),
  ('00000000-0000-0000-0000-00000000a103', 'subscription', 'warning', 'required');

-- How this camera's pan and tilt move the picture, measured rather than assumed: one step of pan
-- shifts the frame by (pan_dx, pan_dy), one of tilt by (tilt_dx, tilt_dy). Sign and magnitude
-- differ per mount, so tracking cannot guess them.
CREATE TABLE ptz_calibration (
  camera_id    uuid PRIMARY KEY REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  pan_dx       real NOT NULL,
  pan_dy       real NOT NULL,
  tilt_dx      real NOT NULL,
  tilt_dy      real NOT NULL,
  confidence   real NOT NULL DEFAULT 0,
  calibrated_at timestamptz NOT NULL DEFAULT now(),
  method       text NOT NULL DEFAULT 'auto'
);

-- Which cameras follow objects on their own, and what they follow.
--
-- Self-guidance is a property of the camera, not of whoever happens to be watching it: a camera
-- with a row here tracks whether or not an operator has the monitor open, and it goes on doing so
-- across a restart of the tracking service. An operator can still take over from the monitor and
-- name a particular object, but that override lives only in memory and expires with the object.
--
-- No row at all means switched off. That is not distinguished from a row with enabled = false, and
-- deliberately: there is nothing a deployment would do differently between the two.
--
-- See PtzTrackingPolicy in proto/vms_cfg.proto.
CREATE TABLE ptz_tracking_policy (
  camera_id uuid    PRIMARY KEY REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  enabled   boolean NOT NULL DEFAULT false,
  -- Object kinds in priority order, comma separated ("person,vehicle"). Empty means any object
  -- will do. Same words as the wire ("person", "face", "vehicle", ...), the way
  -- analytics_watch.classes already stores them.
  kinds     text    NOT NULL DEFAULT '',
  -- Which producers of boxes to believe: "cv" (the detector) and/or "onvif" (the camera's own
  -- metadata). Both by default, because a camera that has one of them configured should not have
  -- to name it here as well.
  sources   text    NOT NULL DEFAULT 'cv,onvif',
  -- Whose objects this camera follows. NULL means its own -- it finds them and moves its own
  -- motor, which is what a self-guiding camera does. Set names a stationary master with analytics,
  -- and this camera becomes a slave aimed through ptz_slave_binding below.
  --
  -- CASCADE, not SET NULL, and the difference is behavioural rather than tidiness. Deleting the
  -- master already takes the survey with it, so SET NULL would leave a camera with self-guidance
  -- switched on and no master -- which does not stop it, it *changes what it does*: it would start
  -- hunting objects in its own picture, and a PTZ camera parked on a fixed zone would begin roaming
  -- because someone deleted a different camera. Dropping the row reads as switched off, so the
  -- slave stops and waits to be set up again, which it needs anyway now that its survey is gone.
  master_camera_id uuid REFERENCES camera_setup(camera_id) ON DELETE CASCADE
);

-- Master/slave aiming: a stationary camera with analytics finds objects, a PTZ camera is pointed
-- at them. See PtzSlaveBinding in proto/vms_cfg.proto.
--
-- The survey tying one slave to one master. Keyed by the slave: it has one motor, so it can only
-- take hints from one place. A master may drive several slaves.
CREATE TABLE ptz_slave_binding (
  slave_camera_id  uuid PRIMARY KEY REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  master_camera_id uuid NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  -- 'absolute' (interpolate a position) | 'presets' (drive to the nearest marked point's preset).
  method           text NOT NULL DEFAULT 'absolute',
  calibrated_at    timestamptz NOT NULL DEFAULT now()
);

-- One marked correspondence. pan/tilt and preset_token are both optional and both are usually
-- present: the operator drives the slave to the spot once, and that single position yields the
-- coordinates (GetStatus) and a preset (SetPreset) alike. Keeping whatever was obtainable is what
-- lets method change later without marking the zones out again.
CREATE TABLE ptz_slave_binding_point (
  slave_camera_id uuid NOT NULL REFERENCES ptz_slave_binding(slave_camera_id) ON DELETE CASCADE,
  ordinal         int  NOT NULL,
  -- 0..1 in the master's frame, from the top-left: the bottom edge of the object's box, where it
  -- meets the ground.
  master_x        real NOT NULL,
  master_y        real NOT NULL,
  pan             real,
  tilt            real,
  preset_token    text,
  preset_name     text,
  PRIMARY KEY (slave_camera_id, ordinal)
);

-- Per-camera video analytics watch. timeout_seconds is the retention timeout for events this watch
-- raises; its default matches onvif_alarm_filter.timeout_seconds so both event sources behave alike.
CREATE TABLE analytics_watch (
  camera_id          uuid    PRIMARY KEY REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  stream_name        text    NOT NULL,
  enabled            boolean NOT NULL DEFAULT false,
  classes            text    NOT NULL DEFAULT 'person',
  min_confidence     real    NOT NULL DEFAULT 0.5,
  sample_fps         integer NOT NULL DEFAULT 1,
  attach_debug_crops boolean NOT NULL DEFAULT false,
  timeout_seconds    integer NOT NULL DEFAULT 10,
  store_embeddings   boolean NOT NULL DEFAULT false
);

-- The deployment's nodes: one row per media server with the services pinned to its machine, which
-- MEDIA_SERVER_PAIRS alone used to name, so that a node joins or leaves without the core restarting.
-- Rows of MEDIA_SERVER_PAIRS are written at every start and marked configured; a node enrolled over
-- the network (EnrollNode) is not, and may be removed.
CREATE TABLE media_server_node (
  server_id   text        PRIMARY KEY,
  fs_id       text        NOT NULL,
  onvif_id    text        NOT NULL,
  logger_id   text        NOT NULL,
  roi_id      text        NOT NULL DEFAULT '',
  name        text        NOT NULL UNIQUE,
  address     text        NOT NULL DEFAULT '',
  media_mtx   boolean     NOT NULL DEFAULT false,
  configured  boolean     NOT NULL DEFAULT false,
  enrolled_at timestamptz NOT NULL DEFAULT now()
);

-- Whether a machine may enroll without an administrator's password: open while the deployment is
-- being set up, closed by the administrator once every machine is in. One row. A fresh database
-- starts open; VmsCfg closes it on a database that already carries an installation.
CREATE TABLE deployment (
  id              int     PRIMARY KEY CHECK (id = 1),
  enrollment_open boolean NOT NULL DEFAULT true
);
INSERT INTO deployment (id) VALUES (1);

-- ── Event routing ──────────────────────────────────────────────────────────────

-- A configured reaction to the journal. The router in VmsSingleton reads these rows and nothing
-- else says what reacts to an event; a change runs the event-subscriptions-changed signal, and
-- `version` grows on every save so a started run keeps the snapshot it began with.
--
-- `source_filter` is the selector over an event's own fields and extra props:
--   {"object_ids": [], "camera_ids": [], "kinds": [], "tokens": [], "props": {}}
-- It is JSON rather than columns because the shape is the operator's: the router and the save-time
-- validator are the only readers, and both know every key. An unknown key is a save-time error,
-- never a rule ignored at run time.
CREATE TABLE event_subscriptions (
  id              uuid    PRIMARY KEY,
  name            text    NOT NULL,
  enabled         boolean NOT NULL DEFAULT true,
  -- A name from the map's journal dictionary: sensor, relay, analytics, alarm_raised,
  -- alarm_acknowledged, alarm_returned, alarm_closed, or the legacy alarm.
  event_name      text    NOT NULL,
  source_filter   jsonb   NOT NULL DEFAULT '{}',
  armed_condition text    NOT NULL DEFAULT 'any'
    CHECK (armed_condition IN ('any', 'armed', 'disarmed')),
  cooldown_ms     integer NOT NULL DEFAULT 0 CHECK (cooldown_ms >= 0),
  version         integer NOT NULL DEFAULT 1
);

-- The actions of one subscription, in the order they run. `params` is per-kind JSON, validated
-- when the subscription is saved; position starts at 0 and is the run's progress marker.
CREATE TABLE subscription_actions (
  subscription_id uuid    NOT NULL REFERENCES event_subscriptions(id) ON DELETE CASCADE,
  position        integer NOT NULL,
  kind            text    NOT NULL CHECK (kind IN
    ('retain_recording', 'set_relay', 'emit_event', 'raise_alarm')),
  params          jsonb   NOT NULL,
  PRIMARY KEY (subscription_id, position)
);
