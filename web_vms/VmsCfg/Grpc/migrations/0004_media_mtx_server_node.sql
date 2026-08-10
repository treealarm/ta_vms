-- Which node a MediaMTX belongs to.
--
-- media_server pushes a camera's stream to the MediaMTX assigned to that camera, so an assignment
-- that ignores the node sends video across whatever link separates them. NULL means "shared":
-- the server answers for any node, which is what a single-box installation has and why existing
-- rows need no migration.
ALTER TABLE media_mtx_server ADD COLUMN IF NOT EXISTS server_id text NULL;
