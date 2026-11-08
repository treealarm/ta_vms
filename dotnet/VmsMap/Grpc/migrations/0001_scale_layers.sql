-- ── The scale layers, named the way a map is read ─────────────────────────────────────────────
-- The first schema seeded every range of zooms 1…19 — 190 rows named "3", "7-12" and so on, which
-- is a dictionary nobody reads. A layer is the map's own name for a scale: OSM's tiles are cut at
-- these zooms, and the names are what an operator recognises. Ranges may share a zoom — a gate
-- lies both on its street and in the building it is set in — and a node is given one layer (an
-- empty one is drawn at every zoom). The demo's own three layers go with them: it no longer writes
-- any, and "Building" there meant another range than the one below.
DELETE FROM levels
WHERE zoom_level ~ '^[0-9]+(-[0-9]+)?$'
   OR zoom_level IN ('Campus', 'Building', 'Rack');

INSERT INTO levels (zoom_level, zoom_min, zoom_max)
SELECT name, lo, hi
FROM (VALUES
  ('World',     0,  3),
  ('Continent', 4,  5),
  ('Country',   6,  7),
  ('Region',    8,  9),
  ('City',     10, 12),
  ('District', 13, 14),
  ('Street',   15, 16),
  ('Building', 17, 18),
  ('Room',     19, 22)
) AS layer(name, lo, hi)
WHERE NOT EXISTS (SELECT 1 FROM levels l WHERE l.zoom_level = layer.name);
