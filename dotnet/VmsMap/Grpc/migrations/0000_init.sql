-- The whole schema of ta_map, one baseline. There were no releases carrying data, so the
-- migrations that grew it one feature at a time are folded into this file and the database is
-- recreated instead of upgraded (docs/plans/square-merge/m2.4a-foundation.md). See
-- docs/maps-and-plans.md and docs/plans/square-merge/README.md, section 4.2.
--
-- Derived from Square's fifteen initdb scripts (multitenant_admin, the single source of that
-- schema), with the integration tables left out and the model changes of the merge applied: one
-- object tree with side tables, typed visual properties, rights as (object, subject, action), and
-- files in the database.
--
-- Not created: integro, integro_types, integro_type_children, action_*, groups. A group is a tree
-- node now; a camera is a node with a placement.

CREATE EXTENSION IF NOT EXISTS postgis;
-- event_name is filtered with LIKE '%...%' in the journal; only a trigram index serves that.
CREATE EXTENSION IF NOT EXISTS pg_trgm;

-- Blobs: plan pictures, diagram backgrounds, event crops. In the database because the service is
-- replicable: a path on a volume would tie a row to the instance that wrote it.
CREATE TABLE files (
  id    uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  mime  text NOT NULL,
  bytes bytea NOT NULL,
  etag  text
);

-- ── The tree ──────────────────────────────────────────────────────────────────────────────────
-- One tree for spaces, objects, cameras and mnemoschemes. parent_id has no foreign key on
-- purpose: deleting a node deletes its subtree explicitly (one recursive statement), and a
-- self-referencing cascade would make a deep tree expensive to remove.
CREATE TABLE objects (
  id        uuid PRIMARY KEY,
  parent_id uuid,
  owner_id  uuid,  -- set on a replica: the node whose states and values this one shows
  name      text NOT NULL
);

CREATE INDEX idx_objects_parent_id ON objects (parent_id);
CREATE INDEX idx_objects_owner_id ON objects (owner_id);

-- Abstract properties only; how a node is drawn lives in `visual`.
CREATE TABLE properties (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  prop_name   varchar NOT NULL,
  str_val     text,
  visual_type varchar,
  object_id   uuid NOT NULL REFERENCES objects (id) ON DELETE CASCADE
);

CREATE INDEX idx_properties_object_id ON properties (object_id);
CREATE INDEX idx_properties_prop_name ON properties (prop_name);
CREATE INDEX idx_properties_prop_name_str_val ON properties (prop_name, str_val);

-- The typed replacement for Square's magic __color/__image*/__text properties.
CREATE TABLE visual (
  object_id    uuid PRIMARY KEY REFERENCES objects (id) ON DELETE CASCADE,
  color        text,
  image        bytea,
  image_width  integer,
  image_height integer,
  rotation_deg double precision,
  text         text
);

-- A right is a row on a node: subject (a Keycloak role or a user name) may perform action.
-- Inheritance downwards and the open action set are M3's; the shape is already theirs.
CREATE TABLE rights (
  object_id uuid NOT NULL REFERENCES objects (id) ON DELETE CASCADE,
  subject   text NOT NULL,
  action    text NOT NULL,
  PRIMARY KEY (object_id, subject, action)
);

CREATE INDEX idx_rights_subject ON rights (subject);

-- ── Scale layers and geometry ────────────────────────────────────────────────────────────────
CREATE TABLE levels (
  id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  zoom_level text NOT NULL,
  zoom_min   integer NOT NULL,
  zoom_max   integer NOT NULL
);

-- What is drawn on a canvas: a point, a polyline or a polygon, in the coordinates of the nearest
-- canvas ancestor. The id is the node's. That ancestor is recorded on every figure so that a
-- query for "what is in this area" runs inside one canvas instead of guessing by the numbers:
-- conditional coordinates of different plans intersect around (0, 0).
CREATE TABLE geo_objects (
  id         uuid PRIMARY KEY REFERENCES objects (id) ON DELETE CASCADE,
  figure     geometry(GEOMETRY, 4326) NOT NULL,
  radius     double precision,
  zoom_level text,
  canvas_id  uuid NULL
);

CREATE INDEX idx_geo_objects_figure_gist ON geo_objects USING gist (figure);
CREATE INDEX idx_geo_objects_canvas_id ON geo_objects (canvas_id);

-- ── Mnemoschemes ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE diagram_types (
  id      uuid PRIMARY KEY,
  name    text NOT NULL UNIQUE,
  file_id uuid REFERENCES files (id)
);

CREATE TABLE diagram_type_regions (
  diagram_type_id uuid NOT NULL REFERENCES diagram_types (id) ON DELETE CASCADE,
  region_key      text NOT NULL,
  geometry        jsonb NOT NULL,
  styles          jsonb,
  PRIMARY KEY (diagram_type_id, region_key)
);

-- The id is the node's; dgr_type is the type's id, not its name, so a rename cannot orphan the
-- diagrams of a type. SET NULL, not the default RESTRICT: a type is a shared description rather
-- than a parent, and deleting it must leave the diagrams where they are, drawn by nothing, until
-- another type is chosen for them.
CREATE TABLE diagrams (
  id        uuid PRIMARY KEY REFERENCES objects (id) ON DELETE CASCADE,
  dgr_type  uuid REFERENCES diagram_types (id) ON DELETE SET NULL,
  geometry  jsonb,
  region_id text,
  file_id   uuid REFERENCES files (id)
);

-- ── Events ───────────────────────────────────────────────────────────────────────────────────
-- Square's events table had no primary key at all; the application treated `id` as one.
CREATE TABLE events (
  id             uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  object_id      uuid REFERENCES objects (id) ON DELETE CASCADE,
  event_name     varchar,
  event_priority integer,
  "timestamp"    timestamptz,
  param0         varchar,
  param1         varchar
);

CREATE TABLE event_props (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  prop_name   varchar,
  str_val     text,
  visual_type varchar,
  owner_id    uuid REFERENCES events (id) ON DELETE CASCADE
);

CREATE INDEX idx_events_timestamp ON events ("timestamp");
CREATE INDEX idx_events_event_name ON events (event_name);
CREATE INDEX idx_events_event_name_trgm ON events USING gin (event_name gin_trgm_ops);
CREATE INDEX idx_events_object_id ON events (object_id);
CREATE INDEX idx_events_priority ON events (event_priority);
CREATE INDEX idx_events_param0 ON events (param0);
CREATE INDEX idx_events_param1 ON events (param1);
CREATE INDEX idx_events_param0_param1 ON events (param0, param1);
-- Keyset pagination: the next page starts after the last (timestamp, id) the caller saw.
CREATE INDEX idx_events_ts_id ON events ("timestamp" DESC, id DESC);
CREATE INDEX idx_events_object_ts_id ON events (object_id, "timestamp" DESC, id DESC);

-- Transactional outbox for the map's own notifications. The event row and the notification that
-- says it exists are written in one transaction, so a crash between them cannot lose the reaction;
-- the publisher is at-least-once and the receiver is idempotent. Published rows are removed.
CREATE TABLE map_outbox (
  id           bigserial PRIMARY KEY,
  topic        text NOT NULL,
  payload      jsonb NOT NULL,
  created_at   timestamptz NOT NULL DEFAULT now(),
  published_at timestamptz,
  attempts     integer NOT NULL DEFAULT 0,
  last_error   text
);

CREATE INDEX ix_map_outbox_pending ON map_outbox (id) WHERE published_at IS NULL;

CREATE INDEX idx_event_props_owner_id ON event_props (owner_id);
CREATE INDEX idx_event_props_prop_name_val ON event_props (owner_id, prop_name, str_val);

-- ── Tracks and values ────────────────────────────────────────────────────────────────────────
CREATE TABLE track_points (
  id           uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  object_id    uuid REFERENCES objects (id) ON DELETE CASCADE,
  "timestamp"  timestamptz NOT NULL,
  figure       geometry(GEOMETRY, 4326) NOT NULL,
  radius       double precision,
  zoom_level   text,
  extra_props  jsonb
);

CREATE INDEX idx_track_points_object_id ON track_points (object_id);
CREATE INDEX idx_track_points_ts ON track_points ("timestamp");
CREATE INDEX idx_track_points_extra_props ON track_points USING gin (extra_props);
CREATE INDEX idx_track_points_figure_gist ON track_points USING gist (figure);

CREATE TABLE db_values (
  id       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  owner_id uuid NOT NULL REFERENCES objects (id) ON DELETE CASCADE,
  name     varchar NOT NULL,
  value    jsonb
);

CREATE INDEX idx_db_values_owner_id ON db_values (owner_id);
CREATE INDEX idx_db_values_name ON db_values (name);

-- ── States and alarms ────────────────────────────────────────────────────────────────────────
-- The id of a state row is the monitored object's, not a key of its own.
CREATE TABLE object_states (
  id          uuid PRIMARY KEY REFERENCES objects (id) ON DELETE CASCADE,
  "timestamp" timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE object_state_values (
  id        uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  object_id uuid NOT NULL REFERENCES object_states (id) ON DELETE CASCADE,
  state     varchar NOT NULL
);

CREATE INDEX idx_object_state_values_object_id ON object_state_values (object_id);

CREATE TABLE object_state_descriptions (
  id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  alarm       boolean NOT NULL DEFAULT false,
  state       varchar NOT NULL UNIQUE,
  state_descr text,
  state_color varchar
);

CREATE TABLE alarm_states (
  id              uuid PRIMARY KEY REFERENCES objects (id) ON DELETE CASCADE,
  alarm           boolean NOT NULL DEFAULT false,
  children_alarms integer NOT NULL DEFAULT 0
);

CREATE INDEX idx_alarm_states_alarm_true ON alarm_states (id) WHERE alarm;

-- The operator's alarm entity, on top of the ALARM state rather than instead of it: the state says
-- "something is wrong here now" and colours the map, this row says "somebody has to deal with it"
-- and carries the status, the acknowledgement and the close reason. Acknowledgement does not clear
-- ALARM: the physical condition is still there.
--
-- cleared_at is when the condition went away, closed_at when the alarm was dealt with; a 'returned'
-- alarm has the first and not the second. Deleting a node cascades its alarms into history, exactly
-- as it does its events: a camera recreated with a new id must not inherit anything.
CREATE TABLE alarms (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  object_id        uuid NOT NULL REFERENCES objects (id) ON DELETE CASCADE,
  -- The definition in VmsCfg's alarm_definitions, and the subscription a subscription-sourced
  -- alarm belongs to (VmsCfg's event_subscriptions). Cross-database, so no foreign key: the raise
  -- checks them against the caches and carries on with a warning when it does not know them.
  definition_id    uuid,
  subscription_id  uuid,
  source           text NOT NULL CHECK (source IN ('sensor', 'retention', 'subscription')),
  -- What makes a second raise of the same condition the same alarm: the part, the retention
  -- episode, the subscription and its subject.
  dedup_key        text NOT NULL,
  condition_active boolean NOT NULL,
  status           varchar NOT NULL CHECK (status IN ('active', 'acknowledged', 'returned', 'closed')),
  severity         text NOT NULL CHECK (severity IN ('warning', 'critical')),
  raised_at        timestamptz NOT NULL DEFAULT now(),
  cleared_at       timestamptz,
  -- Retention: when the episode is projected to end. A renewal moves it.
  expected_end_ts  timestamptz,
  acknowledged_at  timestamptz,
  acknowledged_by  text,
  ack_comment      text,
  closed_at        timestamptz,
  close_reason     text CHECK (close_reason IN
    ('returned_to_normal', 'acknowledged_after_normal', 'auto_returned_to_normal',
     'device_gone', 'false_alarm', 'other')),
  close_comment    text,
  closed_by        text,  -- the operator's name, or 'system'
  source_event_id  uuid
);

-- One open alarm per condition. A partial unique index, not a lookup in code: two deliveries of the
-- same sensor transition race, and only the database can decide which raise created the row.
CREATE UNIQUE INDEX ux_alarms_open_dedup
  ON alarms (object_id, dedup_key) WHERE status <> 'closed';

-- The event router's operation key: one subscription raise per trigger event and action, even
-- after the alarm was closed. Without it a repeat that arrives once the operator has dealt with
-- the row would find no open alarm and raise a second one for the same event. Retention is
-- excluded: it reuses the episode's id for every renewal, which must still move the projected end.
CREATE UNIQUE INDEX ux_alarms_subscription_event
  ON alarms (source_event_id, object_id, dedup_key)
  WHERE source_event_id IS NOT NULL AND source = 'subscription';

-- The two pages GetAlarms reads. Open and closed are separate queries with separate cursors, and
-- one index cannot serve both: the open one orders by raised_at, the closed one by closed_at.
CREATE INDEX ix_alarms_open ON alarms (raised_at DESC, id DESC) WHERE status <> 'closed';
CREATE INDEX ix_alarms_closed_at ON alarms (closed_at DESC, id DESC) WHERE status = 'closed';
CREATE INDEX ix_alarms_object ON alarms (object_id, raised_at DESC);

-- ── Spaces, placements and calibrations ──────────────────────────────────────────────────────
-- A space is a node of `objects` with a row here: its name and parent are the object's, its row
-- says what kind of world it is. A nested plan's footprint is its figure in `geo_objects`, drawn
-- in the coordinates of the nearest canvas ancestor; a plan picture is a table of its own, so that
-- listing spaces never reads the bytes. Nothing stores sizes or positions in plan pixels.
--
-- kind 'map' is a root with map tiles underneath and real coordinates; 'plan' is a root or a nested
-- space drawn on its own picture. real_coordinates is what makes two spaces share geometry: cameras
-- and landmarks in the same space, or in any two spaces with real coordinates, can be aimed at one
-- another; a plan in made-up coordinates is a world of its own.
--
-- Names are unique among sibling spaces in the service, not here: in one tree a space shares its
-- parent with diagrams, cameras and objects, and those may carry the same name.
CREATE TABLE spaces (
  object_id        uuid PRIMARY KEY REFERENCES objects (id) ON DELETE CASCADE,
  kind             text NOT NULL,
  real_coordinates boolean NOT NULL,
  -- Order among siblings: the floors of a building, read bottom to top.
  ordinal          integer NOT NULL DEFAULT 0,
  -- Height of this space's floor above the ground, in metres. Only meaningful where coordinates
  -- are real, so that a camera upstairs and one in the yard agree on heights.
  elevation_m      double precision NOT NULL DEFAULT 0,
  -- What the space shows when it is opened: two corners, fitted into whatever window shows it.
  view_south       double precision NOT NULL,
  view_west        double precision NOT NULL,
  view_north       double precision NOT NULL,
  view_east        double precision NOT NULL
);

-- A plan space's picture and where it lies in that space: three of its corners (top-left,
-- top-right, bottom-left), an affine placement -- position, scale, rotation. A table of its own so
-- that listing spaces never reads the bytes.
CREATE TABLE space_plan (
  space_id  uuid    PRIMARY KEY REFERENCES spaces (object_id) ON DELETE CASCADE,
  mime      text    NOT NULL,
  width_px  integer NOT NULL,
  height_px integer NOT NULL,
  tl_lat    double precision NOT NULL,
  tl_lon    double precision NOT NULL,
  tr_lat    double precision NOT NULL,
  tr_lon    double precision NOT NULL,
  bl_lat    double precision NOT NULL,
  bl_lon    double precision NOT NULL,
  -- sha256 of image, hex. The HTTP ETag of the image and the client's cache key.
  etag      text    NOT NULL,
  image     bytea   NOT NULL
);

-- Where a camera is drawn and which way it looks. The position is its point in `geo_objects`; the
-- numbers here are the ones aiming and drawing the view cone use. A camera may be drawn in several
-- spaces -- the yard map and the detailed plan of its corner -- and exactly one of those, the
-- reference, is where it really stands: aiming, calibration and master/slave go by it, the others
-- are only pictures (replicas of the camera node, Square's owner_id relation).
--
-- The camera id is not a foreign key: cameras live in VmsCfg's database, and the service checks the
-- camera exists when a placement is written (a deliberate cross-service check instead of an FK).
-- The camera node's id is derived from the camera id, so it stays the same across restarts.
CREATE TABLE camera_placement (
  object_id    uuid PRIMARY KEY REFERENCES objects (id) ON DELETE CASCADE,
  camera_id    uuid NOT NULL,
  is_reference boolean NOT NULL DEFAULT true,
  height_m     double precision NOT NULL DEFAULT 3,
  -- Compass bearing: 0 = north, clockwise.
  heading_deg  double precision NOT NULL DEFAULT 0,
  hfov_deg     double precision NOT NULL DEFAULT 60,
  range_m      double precision NOT NULL DEFAULT 30
);
CREATE INDEX idx_camera_placement_camera ON camera_placement (camera_id);
CREATE UNIQUE INDEX idx_camera_placement_reference
  ON camera_placement (camera_id) WHERE is_reference;

-- A named point whose position is known -- a corner, a pole, a hatch -- used to calibrate cameras.
-- Operators never see these. The name and the space are the object's; the point is in geo_objects.
CREATE TABLE landmark (
  object_id uuid PRIMARY KEY REFERENCES objects (id) ON DELETE CASCADE,
  height_m  double precision NOT NULL DEFAULT 0
);

-- Where a PTZ camera was pointing when it was centred on a landmark. pan/tilt are in whatever
-- space the camera answers in and are not interpreted, exactly like ptz_slave_binding_point: the
-- fit is made to these measurements, so degrees and normalised -1..1 work out the same. The fit
-- itself is never stored -- VmsPtzTrackingSingleton computes it from these rows.
CREATE TABLE ptz_geo_observation (
  camera_id   uuid NOT NULL,
  landmark_id uuid NOT NULL REFERENCES landmark (object_id) ON DELETE CASCADE,
  pan         real NOT NULL,
  tilt        real NOT NULL,
  -- How far a PTZ slave zooms for an object at the distance of this landmark: the operator frames
  -- the landmark as the slave should show a person standing there, and records the zoom it took.
  -- Optional and separate from pan and tilt, in the camera's own zoom units. The distance is not
  -- stored: it follows from where the camera and the landmark stand, and moves with them.
  zoom        real NULL,
  PRIMARY KEY (camera_id, landmark_id)
);

-- Where a landmark stands on the ground in a fixed camera's picture: x, y normalised to 0..1 across
-- the frame, the same convention as detection boxes, so any stream of the camera with the same view
-- can be used. The landmark's own height does not take part -- what is marked is its foot. The
-- image-to-ground homography fitted from these rows is never stored; VmsPtzTrackingSingleton
-- computes it, as it does the PTZ fit from ptz_geo_observation.
CREATE TABLE ground_observation (
  camera_id   uuid NOT NULL,
  landmark_id uuid NOT NULL REFERENCES landmark (object_id) ON DELETE CASCADE,
  x           real NOT NULL,
  y           real NOT NULL,
  PRIMARY KEY (camera_id, landmark_id)
);

-- The registry from a real device, or one part of it, to the tree node that stands for it. The
-- node id is derived from the key and the derivation is one-way MD5, so given a node id nobody can
-- say what it is; this table is the reverse lookup. `camera_node` was the camera-only version of
-- it, and the key is wider on purpose: a fire panel must be able to register its zones and relays
-- in the same index without a second registry beside this one.
--
--   source_kind  'camera' today; the device type later
--   source_id    the id in the owning service's table (camera_id)
--   part_kind    'device' for the device as a whole, 'digital_input' / 'relay' for one part
--   part_token   the part's token; '' for the device as a whole
--
-- A device node is one row of part_kind='device'. Pictures of a device are replicas and keep their
-- own camera_placement row; they need no row here.
CREATE TABLE device_node (
  object_id   uuid    PRIMARY KEY REFERENCES objects (id) ON DELETE CASCADE,
  source_kind varchar NOT NULL,
  source_id   uuid    NOT NULL,
  part_kind   varchar NOT NULL,
  part_token  varchar NOT NULL,
  -- Whether the device still declares the part, and whether the device answered the last sweep at
  -- all. They are different facts: a network timeout is not a part disappearing, and only a
  -- successful answer that no longer lists a part may set present=false.
  present     boolean NOT NULL DEFAULT true,
  reachable   boolean NOT NULL DEFAULT true,
  -- The monotonicity guard for device-event: epoch is the publishing process, seq grows per part
  -- within it. Comparing timestamps would compare this machine's clock with another's.
  last_epoch       uuid,
  last_seq         bigint NOT NULL DEFAULT 0,
  last_observed_at timestamptz,
  -- The index the unique key builds also serves every lookup by device: its leftmost columns are
  -- the key, and parts are only ever read for one device at a time.
  UNIQUE (source_kind, source_id, part_kind, part_token)
);

-- What a device says about itself. A snapshot of the hardware, not configuration: the integration
-- owns it, and it is stored rather than read from the event so that a diagram template still
-- matches after a restart. Only an answering camera writes here; a timeout leaves the last known
-- model in place.
CREATE TABLE device_info (
  source_kind  varchar NOT NULL,
  source_id    uuid    NOT NULL,
  manufacturer text    NOT NULL DEFAULT '',
  model        text    NOT NULL DEFAULT '',
  firmware     text    NOT NULL DEFAULT '',
  PRIMARY KEY (source_kind, source_id)
);

-- ── Rights mode ──────────────────────────────────────────────────────────────────────────────
-- The switch that says what a node with no rows means. A fresh installation starts enforced:
-- nobody has been granted anything yet, and a new deployment must not open every camera by
-- default.
CREATE TABLE rights_mode (
  id   boolean PRIMARY KEY DEFAULT true CHECK (id),
  mode text NOT NULL
);

INSERT INTO rights_mode (mode) VALUES ('enforced');

-- ── Demo registry ────────────────────────────────────────────────────────────────────────────
-- The demo world is installed by code, but its rows must be recognizable afterwards: they are the
-- only ones a reinstall or a removal may touch, and a user's own node next to them must survive
-- both. A space's id is assigned by the server (UpsertSpace), so the logical key below is the only
-- thing that says which row is ours on the next install.
CREATE TABLE demo_config (
  demo_key   text PRIMARY KEY,
  version    integer NOT NULL,
  applied_at timestamptz NOT NULL DEFAULT now()
);

-- One row per entity the demo created, under the key the world model names it by:
-- `space/room-101`, `diagram/rack-a-01`, `type/rack`, `file/rack-background`, `level/room`,
-- `camera/sim-master`. The prefix is the kind: it tells a removal what service deletes the row.
-- object_id has no foreign key: rows here name a space, an object, a type, a file or a level.
CREATE TABLE demo_object (
  demo_key    text NOT NULL REFERENCES demo_config (demo_key) ON DELETE CASCADE,
  logical_key text NOT NULL,
  object_id   uuid NOT NULL,
  PRIMARY KEY (demo_key, logical_key)
);

-- ── Seeds ────────────────────────────────────────────────────────────────────────────────────
-- Square inserted these from InitHostedService on first start; a replicable service must not
-- race itself into two default sets, so they belong to the migration that creates the tables.
INSERT INTO object_state_descriptions (state, state_descr, state_color, alarm) VALUES
  ('CONNECTED',    'Connected',    '#00FF00', false),
  ('DISCONNECTED', 'Disconnected', '#FF0000', true),
  ('ALARM',        'Alarm',        '#FF0000', true),
  ('INFO',         'Info',         '#00FF00', false),
  ('NORM',         'Normal',       '#0000FF', false),
  -- Device parts. Without a row here the map has neither a colour nor the alarm flag, and an
  -- unknown state is not shown at all. None of them is an alarm by itself: whether an open contact
  -- alarms is the operator's setting, and the alarm entity is M2.4c.
  ('SENSOR_OPEN',  'Open',         '#FF9900', false),
  ('SENSOR_CLOSED','Closed',       '#00AA00', false),
  ('RELAY_ACTIVE', 'Active',       '#3399FF', false),
  ('RELAY_INACTIVE','Inactive',    '#808080', false);

-- The scale layers: every pair (j, i) with j <= i, named "j" when j = i and "j-i" otherwise.
INSERT INTO levels (zoom_level, zoom_min, zoom_max)
SELECT CASE WHEN j = i THEN i::text ELSE j::text || '-' || i::text END, j, i
FROM generate_series(1, 19) AS j
CROSS JOIN generate_series(1, 19) AS i
WHERE i >= j;
