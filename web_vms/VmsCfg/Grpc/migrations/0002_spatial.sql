-- Maps & plans: where cameras and reference points are in space. See docs/maps-and-plans.md.
--
-- A space is one world things are placed in: a real map, or a plan (a picture of a yard, a building,
-- a floor, a room). Spaces nest -- a plan's footprint is drawn on its parent and the operator dives
-- into it -- but each has its own coordinates: a plan is never laid over the map it sits in.
--
-- Every position is WGS84 latitude/longitude plus a height in metres above the floor of the space
-- it is in -- never above sea level. A plan in made-up coordinates still stores WGS84, made up
-- around (0, 0), so one code path serves every space. Nothing here stores pixels of a plan image:
-- objects keep their place when a plan is re-aligned.

-- kind 'map' is a root with map tiles underneath and real coordinates; 'plan' is a root or a nested
-- space drawn on its own picture. real_coordinates is what makes two spaces share geometry: cameras
-- and landmarks in the same space, or in any two spaces with real coordinates, can be aimed at one
-- another; a plan in made-up coordinates is a world of its own.
CREATE TABLE space (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  parent_id        uuid REFERENCES space(id) ON DELETE CASCADE,
  name             text NOT NULL,
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
  view_east        double precision NOT NULL,
  -- Where this space lies on its parent, as a polygon in the parent's coordinates:
  -- [[lat, lon], ...]. The operator dives in by clicking it. NULL for a root, or for a nested space
  -- reached only from the list.
  footprint        jsonb,
  -- Roots too: two maps called "Main" would be indistinguishable in a list.
  UNIQUE NULLS NOT DISTINCT (parent_id, name)
);
CREATE INDEX idx_space_parent ON space (parent_id, ordinal);

-- A plan space's picture and where it lies in that space: three of its corners (top-left,
-- top-right, bottom-left), an affine placement -- position, scale, rotation. A table of its own so
-- that listing spaces never reads the bytes.
CREATE TABLE space_plan (
  space_id  uuid    PRIMARY KEY REFERENCES space(id) ON DELETE CASCADE,
  mime      text    NOT NULL,
  width_px  integer NOT NULL,
  height_px integer NOT NULL,
  tl_lat    double precision NOT NULL,
  tl_lon    double precision NOT NULL,
  tr_lat    double precision NOT NULL,
  tr_lon    double precision NOT NULL,
  bl_lat    double precision NOT NULL,
  bl_lon    double precision NOT NULL,
  -- sha256 of image, hex. The HTTP ETag of the image and the client's cache key.
  etag      text    NOT NULL,
  image     bytea   NOT NULL
);

-- Where a camera is drawn and which way it looks. A camera may be drawn in several spaces -- the
-- yard map and the detailed plan of its corner -- and exactly one of those, the reference, is where
-- it really stands: aiming, calibration and master/slave go by it, the others are only pictures
-- (Square's owner_id relation). heading_deg is a compass bearing: 0 = north, clockwise (Square's
-- __image_rotate). hfov_deg and range_m only draw the view cone; nothing is aimed through them.
CREATE TABLE camera_placement (
  camera_id    text    NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  space_id     uuid    NOT NULL REFERENCES space(id) ON DELETE CASCADE,
  is_reference boolean NOT NULL DEFAULT false,
  lat          double precision NOT NULL,
  lon          double precision NOT NULL,
  height_m     double precision NOT NULL DEFAULT 3,
  heading_deg  double precision NOT NULL DEFAULT 0,
  hfov_deg     double precision NOT NULL DEFAULT 60,
  range_m      double precision NOT NULL DEFAULT 30,
  PRIMARY KEY (camera_id, space_id)
);
CREATE INDEX idx_camera_placement_space ON camera_placement (space_id);
CREATE UNIQUE INDEX idx_camera_placement_reference ON camera_placement (camera_id) WHERE is_reference;

-- A named point whose position is known -- a corner, a pole, a hatch -- used to calibrate cameras.
-- Operators never see these.
CREATE TABLE landmark (
  id       uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  space_id uuid NOT NULL REFERENCES space(id) ON DELETE CASCADE,
  name     text NOT NULL,
  lat      double precision NOT NULL,
  lon      double precision NOT NULL,
  height_m double precision NOT NULL DEFAULT 0
);
CREATE INDEX idx_landmark_space ON landmark (space_id);

-- Where a PTZ camera was pointing when it was centred on a landmark. pan/tilt are in whatever
-- space the camera answers in and are not interpreted, exactly like ptz_slave_binding_point: the
-- fit is made to these measurements, so degrees and normalised -1..1 work out the same. The fit
-- itself is never stored -- VmsPtzTrackingSingleton computes it from these rows.
CREATE TABLE ptz_geo_observation (
  camera_id   text NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  landmark_id uuid NOT NULL REFERENCES landmark(id) ON DELETE CASCADE,
  pan         real NOT NULL,
  tilt        real NOT NULL,
  PRIMARY KEY (camera_id, landmark_id)
);
