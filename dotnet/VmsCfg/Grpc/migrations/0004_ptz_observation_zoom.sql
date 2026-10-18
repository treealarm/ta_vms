-- How far a PTZ slave zooms for an object at the distance of this landmark: the operator frames the
-- landmark as the slave should show a person standing there, and records the zoom it took. Optional
-- and separate from pan and tilt, which are best recorded zoomed right in on the landmark. In the
-- camera's own zoom units, uninterpreted, like pan and tilt. The distance is not stored: it follows
-- from where the camera and the landmark stand, and moves with them.
ALTER TABLE ptz_geo_observation ADD COLUMN zoom real NULL;
