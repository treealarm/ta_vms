# Maps & plans

ta_vms places cameras and reference points in space on its own, without Square. An administrator
lays out maps and plans under **Admin → Maps**; an operator watches cameras where they stand under
**Operator → Map**, dives into nested plans, and can point a calibrated PTZ at a place by clicking it.

The same positions are what lets the VMS aim cameras: a PTZ calibrated against landmarks turns to
any point that shares its coordinates, and (next stage) a slave follows a master's objects through
those coordinates instead of a hand-marked pixel survey.

## Model

The shape is Square's diagrams: a tree of worlds, each drawn on its own background, each nested
one reachable from its outline on the parent.

| Entity | Table | Meaning |
|---|---|---|
| Space | `space` | One world things are placed in. `map`: a root over map tiles, real coordinates. `plan`: a root or a nested space drawn on its own picture, in real or made-up coordinates. Has a default view, an order among siblings, a floor elevation and — when nested — a footprint polygon on its parent. |
| Plan | `space_plan` | A plan space's picture and three of its corners in that space (top-left, top-right, bottom-left). |
| Placement | `camera_placement` | Where a camera is drawn in one space: lat/lon, height, heading, view angle, range. One per camera and space; at most one per camera is its **reference**. |
| Landmark | `landmark` | A named point with a known position and height in a space, used only to calibrate cameras. |
| Observation | `ptz_geo_observation` | Where a PTZ pointed (its own pan/tilt units) when centred on a landmark. |

Conventions that hold everywhere:

- **Spaces nest, coordinates do not.** A nested plan is never laid over its parent's map: its
  footprint on the parent is only the way in. The floors of a building are sibling spaces; the
  switcher on the map's edge moves between them, breadcrumbs move up, footprints move down. When
  footprints overlap (floors sharing one outline), a click asks which one.
- **Real or made-up coordinates.** A map, and any plan marked real, is in WGS84. A plan in made-up
  coordinates still stores WGS84, made up around (0, 0), so one code path serves every space — but
  its numbers mean nothing anywhere else.
- **Two spaces share geometry when they are the same space or both are real.** That rule decides
  everything that crosses spaces: which landmarks may calibrate a camera, which points a camera can
  be aimed at, and (stage 3) which masters can drive which slaves. VmsCfg (`SpatialDb`), the
  tracking service (`CameraSpatialModel.SharesGeometryWith`) and the client (`sharesGeometry`)
  apply the same rule.
- **A camera has one reference placement and any number of pictures.** The reference is where it
  really stands; aiming and calibration go by it. Pictures — the camera drawn again on a detailed
  plan of its corner — are only drawn. This is Square's `owner_id` relation: export makes the
  reference the camera object and each picture an object owned by it. A camera's first placement
  becomes its reference; clearing the flag leaves the camera with none, never quietly promoting
  another.
- **Positions are WGS84 plus a height in metres above the floor of their space** — never above sea
  level. Height above the ground is `space.elevation_m + height_m`; elevation matters only where
  coordinates are real, so a camera upstairs and one in the yard agree on heights.
- **A space opens on its default view** (`view_*`: south-west and north-east corners), fitted into
  whatever window shows it, so every screen starts at the same level of detail. A new map is framed
  in its creation form; "Open the space on this view" in the editor reframes any space.
- **Nothing is stored in plan pixels.** Re-aligning a plan moves the picture, not the objects.
- **Heading is a compass bearing**: 0 = north, clockwise — the same as Square's `__image_rotate`.
- **Fits are never stored.** VmsCfg keeps raw observations; `VmsPtzTrackingSingleton` fits them on
  demand, exactly as it does the slave binding.
- Geometry is measured in an equirectangular frame centred on the camera (`Geo/EnuFrame.cs`):
  millimetres of error across a few hundred metres, about a metre at 2 km.

## Where things live

- **VmsCfg** owns the tables (`Grpc/migrations/0002_spatial.sql`, `Grpc/SpatialDb.cs`,
  `Grpc/CfgServiceImpl.Spatial.cs`) and the gRPC contract (`proto/vms_cfg.proto`, section
  "Maps & plans"). Plan images are `bytea` in its database and travel as chunked gRPC streams,
  because Dapr service invocation caps one message at 4 MiB. Limit: 20 MiB per image.
- **VmsPtzTrackingSingleton** owns the geometry (`Geo/`): the local frame, bearings, the
  pan/tilt-against-azimuth/elevation fit with leave-one-out residuals, and `AimAtWorldPoint`. It is
  the one owner of a camera's motor, so map aiming goes through it like every other aim.
- **web_vms.Server** serves `api/spatial/*` (spaces, plan images, placements, landmarks),
  `api/ptz-geo/*` (observations, fit, aim) and `api/map/tiles/*`.
- **web_vms.client**: Leaflet + react-leaflet 5. `src/map/` holds the map primitives (rotated plan
  layer, authenticated tiles, metric grid, markers, footprints); `src/components/maps/` the two tabs.

## Events

- A placement or a camera's observations change → `server-config-changed` with that camera, as
  for any camera setting. Removing a space announces every camera that had a placement in it or in
  anything nested in it.
- A space, its plan or a landmark changes → **`space-config-changed`**, payload `{"space_id": "..."}`.
  Its own topic because it concerns no node: `server-config-changed` with an empty camera list would
  make every media server, VmsFs and VmsOnvif reconcile for nothing. Addressing only, like every
  event here. Today nobody subscribes; the aiming service will once it caches spatial models for
  per-detection aiming (stage 3), and the Square producer once it exports plans.

## Map tiles

`VMS_MAP_TILE_URL` (optional, a `{z}/{x}/{y}` template) names the provider; `VMS_MAP_TILE_ATTRIBUTION`
the credit line it requires. Browsers never call the provider: web_vms proxies every tile and keeps
it in the Dapr state store for 30 days, shared by all replicas (web_vms keeps no local files), sends
an identifying User-Agent and runs at most two downloads at a time per process. That is what the
OpenStreetMap tile policy asks for; it still allows only light use, so a larger deployment points
the variable at its own tile server or a commercial provider. Empty means maps are drawn without
tiles. Expect a cold area to fill in over several seconds the first time it is opened.

Plans never show tiles to operators. While an administrator lines up a plan in real coordinates,
"Map underneath" shows them for that purpose only.

## Calibrating a PTZ against landmarks

1. Place the camera where it really stands (its reference placement) and set its height above the
   floor.
2. Drop landmarks in the same space at points the camera can see — corners, poles, hatches — with
   their height above the floor.
3. In the camera's panel, *Calibrate by landmarks* (offered on the reference placement only): turn the camera until a landmark sits in the
   crosshair and record it. Two landmarks in clearly different directions make a calibration; a
   third lets each be checked (the "Check" column is the leave-one-out miss — redo the largest).
4. *Check* sends the camera to a landmark from the calibration alone. This is also the test that
   the camera obeys absolute positions at all.

What the fit assumes: a level mount, so pan follows the bearing and tilt the elevation, each by a
straight line in the camera's own units. A camera hung out of level shows up as a pattern of misses
and should be levelled. The camera's pan seam (where its range wraps) must not fall between the
landmarks; landmarks spread over the area the camera will actually be aimed at keep it out.
Cameras whose `GetStatus` is a stub are refused with a message — they can still be aimed by presets
through the slave binding.

## Ownership with Square

A camera whose reference placement is in a space with real coordinates owns its position in Square:
its point and heading go up with every push. Any other camera — not placed, placed only as pictures,
or standing on a plan in made-up coordinates — leaves its position to Square: it gets a default point
once, when Square first learns of it, and afterwards only its name and properties are sent.

## Next stages

- **Master/slave through coordinates.** A fixed camera calibrated against ground landmarks
  (image→floor homography, ≥ 4 points); a new binding method `geo` aims a slave at the world point
  under the master's detection, when the two cameras' reference spaces share geometry. Needs a
  spatial-model cache in the aiming service, invalidated by `space-config-changed`.
- **Detections on the map**, projected through the same homography in the browser.
- **Aerial targets**: a master's full pose (PnP from landmarks with heights) gives a ray; a
  co-located slave follows its direction, two masters triangulate a 3D point.
- **Export to Square**: each space a diagram (`background_img`, its footprint the `geometry` on the
  parent), each reference placement the camera object, each picture an object with `owner_id` set
  to the camera.
- A footprint's colour from the state of the cameras inside it; walls that cut view cones; zoom by
  distance.
