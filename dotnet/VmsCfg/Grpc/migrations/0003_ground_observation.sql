-- Where a landmark stands on the ground in a fixed camera's picture: x, y normalised to 0..1 across
-- the frame, the same convention as detection boxes, so any stream of the camera with the same view
-- can be used. The landmark's own height does not take part -- what is marked is its foot. The
-- image-to-ground homography fitted from these rows is never stored; VmsPtzTrackingSingleton
-- computes it, as it does the PTZ fit from ptz_geo_observation.
CREATE TABLE ground_observation (
  camera_id   text NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  landmark_id uuid NOT NULL REFERENCES landmark(id) ON DELETE CASCADE,
  x           real NOT NULL,
  y           real NOT NULL,
  PRIMARY KEY (camera_id, landmark_id)
);
