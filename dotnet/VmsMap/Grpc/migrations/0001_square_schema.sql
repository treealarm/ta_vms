-- The map schema. Derived from Square's fifteen initdb scripts (multitenant_admin, the single
-- source of that schema), with the integration tables left out and the model changes of the
-- merge applied: one object tree with side tables, typed visual properties, rights as
-- (object, subject, action), and files in the database.
--
-- Not created: integro, integro_types, integro_type_children, action_*, groups. A group is a
-- tree node now; a camera is a node with a placement (M2); what a node is, its side rows say.

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
-- canvas ancestor. The id is the node's.
CREATE TABLE geo_objects (
  id         uuid PRIMARY KEY REFERENCES objects (id) ON DELETE CASCADE,
  figure     geometry(GEOMETRY, 4326) NOT NULL,
  radius     double precision,
  zoom_level text
);

CREATE INDEX idx_geo_objects_figure_gist ON geo_objects USING gist (figure);

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
-- diagrams of a type.
CREATE TABLE diagrams (
  id        uuid PRIMARY KEY REFERENCES objects (id) ON DELETE CASCADE,
  dgr_type  uuid REFERENCES diagram_types (id),
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

-- ── Seeds ────────────────────────────────────────────────────────────────────────────────────
-- Square inserted these from InitHostedService on first start; a replicable service must not
-- race itself into two default sets, so they belong to the migration that creates the tables.
INSERT INTO object_state_descriptions (state, state_descr, state_color, alarm) VALUES
  ('CONNECTED',    'Connected',    '#00FF00', false),
  ('DISCONNECTED', 'Disconnected', '#FF0000', true),
  ('ALARM',        'Alarm',        '#FF0000', true),
  ('INFO',         'Info',         '#00FF00', false),
  ('NORM',         'Normal',       '#0000FF', false);

-- The scale layers: every pair (j, i) with j <= i, named "j" when j = i and "j-i" otherwise.
INSERT INTO levels (zoom_level, zoom_min, zoom_max)
SELECT CASE WHEN j = i THEN i::text ELSE j::text || '-' || i::text END, j, i
FROM generate_series(1, 19) AS j
CROSS JOIN generate_series(1, 19) AS i
WHERE i >= j;
