-- Maps & plans on the one tree. See docs/maps-and-plans.md; the model is in the square-merge plan
-- (docs/plans/square-merge/README.md, section 4.2).
--
-- A space is a node of `objects` with a row here: its name and parent are the object's, its row
-- says what kind of world it is. A nested plan's footprint is its figure in `geo_objects`, drawn
-- in the coordinates of the nearest canvas ancestor; a plan picture is a table of its own, so that
-- listing spaces never reads the bytes. Nothing stores sizes or positions in plan pixels.
--
-- A camera is one node of the tree, its id derived from the camera id; `camera_placement` is where
-- it is drawn, and `is_reference` marks the one placement that says where it really stands. The
-- other placements are pictures -- replicas of the camera node (`objects.owner_id`) -- with rows of
-- their own for how they are drawn.
--
-- A landmark is a node too: its name and space are the object's, its height is here, its point is
-- in `geo_objects`. Observations are calibration data keyed by the camera id and the landmark; the
-- fits themselves are never stored (VmsPtzTrackingSingleton computes them from these rows).

-- kind 'map' is a root with map tiles underneath and real coordinates; 'plan' is a root or a nested
-- space drawn on its own picture. real_coordinates is what makes two spaces share geometry: cameras
-- and landmarks in the same space, or in any two spaces with real coordinates, can be aimed at one
-- another; a plan in made-up coordinates is a world of its own.
--
-- Names are unique among sibling spaces in the service, not here: in one tree a space shares its
-- parent with diagrams, cameras and objects, and those may carry the same name.
CREATE TABLE spaces (
  object_id        uuid PRIMARY KEY REFERENCES objects (id) ON DELETE CASCADE,
  kind             text NOT NULL,
  real_coordinates boolean NOT NULL,
  -- Order among siblings: the floors of a building, read bottom to top.
  ordinal          integer NOT NULL DEFAULT 0,
  -- Height of this space's floor above the ground, in metres. Only meaningful where coordinates
  -- are real, so that a camera upstairs and one in the yard agree on heights.
  elevation_m      double precision NOT NULL DEFAULT 0,
  -- What the space shows when it is opened: two corners, fitted into whatever window shows it.
  view_south       double precision NOT NULL,
  view_west        double precision NOT NULL,
  view_north       double precision NOT NULL,
  view_east        double precision NOT NULL
);

-- A plan space's picture and where it lies in that space: three of its corners (top-left,
-- top-right, bottom-left), an affine placement -- position, scale, rotation. A table of its own so
-- that listing spaces never reads the bytes.
CREATE TABLE space_plan (
  space_id  uuid    PRIMARY KEY REFERENCES spaces (object_id) ON DELETE CASCADE,
  mime      text    NOT NULL,
  width_px  integer NOT NULL,
  height_px integer NOT NULL,
  tl_lat    double precision NOT NULL,
  tl_lon    double precision NOT NULL,
  tr_lat    double precision NOT NULL,
  tr_lon    double precision NOT NULL,
  bl_lat    double precision NOT NULL,
  bl_lon    double precision NOT NULL,
  -- sha256 of the image, hex. The HTTP ETag of the image and the client's cache key.
  etag      text    NOT NULL,
  image     bytea   NOT NULL
);

-- Where a camera is drawn and which way it looks. The position is its point in `geo_objects`; the
-- numbers here are the ones aiming and drawing the view cone use. A camera may be drawn in several
-- spaces -- the yard map and the detailed plan of its corner -- and exactly one of those, the
-- reference, is where it really stands: aiming, calibration and master/slave go by it, the others
-- are only pictures (replicas of the camera node, Square's owner_id relation).
--
-- The camera id is not a foreign key: cameras live in VmsCfg's database, and the service checks the
-- camera exists when a placement is written (a deliberate cross-service check instead of an FK).
-- The camera node's id is derived from the camera id, so it stays the same across restarts.
CREATE TABLE camera_placement (
  object_id    uuid PRIMARY KEY REFERENCES objects (id) ON DELETE CASCADE,
  camera_id    text NOT NULL,
  is_reference boolean NOT NULL DEFAULT true,
  height_m     double precision NOT NULL DEFAULT 3,
  -- Compass bearing: 0 = north, clockwise.
  heading_deg  double precision NOT NULL DEFAULT 0,
  hfov_deg     double precision NOT NULL DEFAULT 60,
  range_m      double precision NOT NULL DEFAULT 30
);
CREATE INDEX idx_camera_placement_camera ON camera_placement (camera_id);
CREATE UNIQUE INDEX idx_camera_placement_reference
  ON camera_placement (camera_id) WHERE is_reference;

-- A named point whose position is known -- a corner, a pole, a hatch -- used to calibrate cameras.
-- Operators never see these. The name and the space are the object's; the point is in geo_objects.
CREATE TABLE landmark (
  object_id uuid PRIMARY KEY REFERENCES objects (id) ON DELETE CASCADE,
  height_m  double precision NOT NULL DEFAULT 0
);

-- Where a PTZ camera was pointing when it was centred on a landmark. pan/tilt are in whatever
-- space the camera answers in and are not interpreted, exactly like ptz_slave_binding_point: the
-- fit is made to these measurements, so degrees and normalised -1..1 work out the same. The fit
-- itself is never stored -- VmsPtzTrackingSingleton computes it from these rows.
CREATE TABLE ptz_geo_observation (
  camera_id   text NOT NULL,
  landmark_id uuid NOT NULL REFERENCES landmark (object_id) ON DELETE CASCADE,
  pan         real NOT NULL,
  tilt        real NOT NULL,
  -- How far a PTZ slave zooms for an object at the distance of this landmark: the operator frames
  -- the landmark as the slave should show a person standing there, and records the zoom it took.
  -- Optional and separate from pan and tilt, in the camera's own zoom units. The distance is not
  -- stored: it follows from where the camera and the landmark stand, and moves with them.
  zoom        real NULL,
  PRIMARY KEY (camera_id, landmark_id)
);

-- Where a landmark stands on the ground in a fixed camera's picture: x, y normalised to 0..1 across
-- the frame, the same convention as detection boxes, so any stream of the camera with the same view
-- can be used. The landmark's own height does not take part -- what is marked is its foot. The
-- image-to-ground homography fitted from these rows is never stored; VmsPtzTrackingSingleton
-- computes it, as it does the PTZ fit from ptz_geo_observation.
CREATE TABLE ground_observation (
  camera_id   text NOT NULL,
  landmark_id uuid NOT NULL REFERENCES landmark (object_id) ON DELETE CASCADE,
  x           real NOT NULL,
  y           real NOT NULL,
  PRIMARY KEY (camera_id, landmark_id)
);

-- Coordinates are always those of the nearest canvas ancestor, and conditional coordinates of
-- different plans intersect around (0, 0). Storing that ancestor on every figure lets a query for
-- "what is in this area" run inside one canvas instead of guessing by the numbers.
ALTER TABLE geo_objects ADD COLUMN canvas_id uuid NULL;
CREATE INDEX idx_geo_objects_canvas_id ON geo_objects (canvas_id);
