-- Object ids ("obj-1", "obj-2", ...) used to come from a counter seeded once, at startup, from
-- MAX(object_id) and then incremented in memory. That is only safe while exactly one process ever
-- hands them out: a second one — a replica, or simply a stale process nobody noticed — starts from
-- the same seed and gives two different physical objects the same id. object_id is written to
-- detections and travels to retention_events, so the collision lands in history and cannot be
-- repaired afterwards.
--
-- The sequence takes that job over. INCREMENT BY 100 makes each nextval() hand out a whole block of
-- numbers to one process, which then issues them from memory: the database is touched once per
-- hundred new objects instead of on the hot path, and two processes can never be inside the same
-- block. The cost is a gap in the numbering after a restart, which is harmless — these are labels,
-- not a count of anything.
CREATE SEQUENCE IF NOT EXISTS detections_object_num_seq AS bigint INCREMENT BY 100 START WITH 1;

-- Continue past the highest id ever assigned rather than restarting at 1. is_called = false means
-- the next nextval() returns exactly this value, so with an empty table the first block starts at 1.
SELECT setval(
  'detections_object_num_seq',
  (SELECT COALESCE(MAX(substring(object_id from 5)::bigint), 0) + 1
   FROM detections
   WHERE object_id ~ '^obj-[0-9]+$'),
  FALSE);
