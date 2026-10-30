-- A camera is one tree node whose id is derived from the camera id, but the derivation is a
-- one-way MD5: given a node id nobody can say which camera it is. A placed camera has a
-- camera_placement row to answer that; a camera that has never been placed has none, and the map,
-- the tree and the watch stream still have to name the camera behind the node. This table is that
-- index. Pictures of a camera keep their own camera_placement row and need no row here.
CREATE TABLE camera_node (
  object_id uuid PRIMARY KEY REFERENCES objects (id) ON DELETE CASCADE,
  camera_id text NOT NULL
);

CREATE UNIQUE INDEX idx_camera_node_camera_id ON camera_node (camera_id);

-- Every camera placed before this migration already has its canonical node and a reference row;
-- backfill the index from them. Pictures are is_reference = false and need no row.
INSERT INTO camera_node (object_id, camera_id)
SELECT object_id, camera_id FROM camera_placement WHERE is_reference
ON CONFLICT DO NOTHING;
