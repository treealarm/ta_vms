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
