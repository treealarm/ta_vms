-- A picture is a picture of an original, one level deep. Every read that answers through an owner
-- -- states, values, camera id, node kind, rights -- resolves it in one hop, so the end of a chain
-- answered nothing. Writes flatten the owner now; this flattens the rows written before that rule.
--
-- The walk is seeded once per replica and follows owner_id to the node that has none. The depth
-- cap keeps a cycle written before the rule from spinning: such a replica has no root and is left
-- as it is, for the write path to refuse on its next edit.
WITH RECURSIVE walk AS (
  SELECT r.id AS replica_id, o.id AS current_id, o.owner_id, 1 AS depth
    FROM objects r
    JOIN objects o ON o.id = r.owner_id
   WHERE r.owner_id IS NOT NULL
  UNION ALL
  SELECT w.replica_id, o.id, o.owner_id, w.depth + 1
    FROM walk w
    JOIN objects o ON o.id = w.owner_id
   WHERE w.depth < 32
)
UPDATE objects r
   SET owner_id = w.current_id
  FROM walk w
 WHERE r.id = w.replica_id
   AND w.owner_id IS NULL
   AND w.current_id <> r.id;
