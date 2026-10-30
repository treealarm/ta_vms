-- The spatial module moved to VmsMap (square-merge M2): spaces, plans, placements, landmarks and
-- calibrations live in the map service's database now, on its one tree. No data is carried over --
-- the installations at this version are test ones -- so the tables go, data included. The files
-- 0002..0004 stay: an installation that ran them records their names, and the migrator only
-- applies what is missing.

DROP TABLE IF EXISTS ground_observation;
DROP TABLE IF EXISTS ptz_geo_observation;
DROP TABLE IF EXISTS landmark;
DROP TABLE IF EXISTS camera_placement;
DROP TABLE IF EXISTS space_plan;
DROP TABLE IF EXISTS space;
