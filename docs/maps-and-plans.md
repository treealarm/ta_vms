# Maps & plans

ta_vms places cameras and reference points in space on its own, without Square. An administrator
lays out maps and plans under **Admin → Maps**; an operator watches cameras where they stand under
**Operator → Map**, dives into nested plans, and can point a calibrated PTZ at a place by clicking it.

The same positions are what lets the VMS aim cameras and place what they see: a PTZ calibrated
against landmarks turns to any point that shares its coordinates, a fixed camera marked against
landmarks shows its live detections on the map, and (next stage) a slave follows a master's objects
through those coordinates instead of a hand-marked pixel survey.

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
| Ground mark | `ground_observation` | Where a landmark stands on the ground in a fixed camera's picture, normalised 0..1 like detection boxes. |

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
- **Fits are never stored.** VmsCfg keeps raw observations and marks; `VmsPtzTrackingSingleton`
  fits them on demand, exactly as it does the slave binding.
- Geometry is measured in an equirectangular frame centred on the camera (`Geo/EnuFrame.cs`):
  millimetres of error across a few hundred metres, about a metre at 2 km.

## Where things live

- **VmsCfg** owns the tables (`Grpc/migrations/0002_spatial.sql`, `0003_ground_observation.sql`,
  `Grpc/SpatialDb.cs`, `Grpc/CfgServiceImpl.Spatial.cs`) and the gRPC contract
  (`proto/vms_cfg.proto`, section "Maps & plans"). Plan images are `bytea` in its database and travel as chunked gRPC streams,
  because Dapr service invocation caps one message at 4 MiB. Limit: 20 MiB per image.
- **VmsPtzTrackingSingleton** owns the geometry (`Geo/`): the local frame, bearings, the
  pan/tilt-against-azimuth/elevation fit with leave-one-out residuals, `AimAtWorldPoint`, and the
  picture-to-ground homography of a fixed camera (`GroundHomography`). It is the one owner of a
  camera's motor, so map aiming goes through it like every other aim; the homography lives beside
  the PTZ fit because the master/slave stage needs both in one place.
- **web_vms.Server** serves `api/spatial/*` (spaces, plan images, placements, landmarks),
  `api/ptz-geo/*` (observations, fit, aim), `api/ground-geo/*` (marks, fit, and the mappings a map
  needs) and `api/map/tiles/*`.
- **web_vms.client**: Leaflet + react-leaflet 5. `src/map/` holds the map primitives (rotated plan
  layer, authenticated tiles, metric grid, markers, footprints); `src/components/maps/` the two tabs.

## Events

- A placement or a camera's observations or marks change → `server-config-changed` with that camera, as
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
and should be levelled; one mounted on its side or at an angle cannot be calibrated by this fit at
all (see "PTZ fit by mount orientation" under the next stages). The camera's pan seam (where its range wraps) must not fall between the
landmarks; landmarks spread over the area the camera will actually be aimed at keep it out.
Cameras whose `GetStatus` is a stub are refused with a message — they can still be aimed by presets
through the slave binding.

## Calibrating a fixed camera against the ground

A fixed camera sees the floor through a projective transform, so four points known both in its
picture and on the ground fix the mapping between them, whatever the mount, tilt or lens — no
camera model, and no need for the camera's own position to be exact.

1. Place the camera where it really stands (its reference placement). The calibration is offered
   there, and for a camera without PTZ the same button opens this one.
2. Drop landmarks on the ground the camera sees — the same landmarks a PTZ uses.
3. Pick a landmark, click where it **stands** in the live picture — its foot, not its top; its
   height plays no part here — and go on to the next. Marks can be dragged to correct them.
4. Four landmarks spread over the ground, no three on one line, make a mapping. From the fifth,
   the table shows how far the mapping puts each landmark from where it stands, in metres, and the
   map draws that miss as a red line. Once mapped, the pointer over the picture and the live
   detections show on the map beside it.

The miss is taken against the fit of all the marks, as georeferencing tools do, not leave-one-out as
the PTZ fit is: a homography has eight degrees of freedom, so with a handful of marks leaving out a
corner one makes the rest extrapolate, and a single misplaced landmark showed as a 50 m miss on a
correct corner. All the marks must be on one floor (spaces of equal elevation) — the mapping is of
one plane. Lens distortion is not modelled: a wide lens misses more towards the edges of the frame,
which the misses show; landmarks spread over the part of the picture that matters keep it small
there.

## Detections on the map

The operator's map shows the live detections of every fixed camera mapped onto the ground whose
reference placement shares geometry with the space on screen (VmsCfg decides which, by the same
rule as everything else). "Detections" in the map's corner turns them off and on.

- The browser gets each camera's mapping as a 3×3 matrix straight to WGS84 (`GET
  api/ground-geo?spaceId=`) and only multiplies by it; the fit is the server's. Detections come
  from the same live stream the video overlay uses (`api/analytics/live`), with the same fade.
- The point projected is the bottom middle of the box — feet, wheels. Faces and plates are not
  drawn: they sit on a person or vehicle that already is.
- A point beyond the horizon, or farther than the camera's range on its placement, is dropped: near
  the horizon one pixel spans kilometres, and such a point says more about the fit than about the
  object.
- A dot has its track's colour, the same as its box on the video, so the two can be matched.

## Alarms on the map

An alarm here is what the rest of ta_vms already calls one: a retention episode in VmsFs, opened
and renewed by `RetainEvent` (VmsOnvif for a matched ONVIF event, VmsAnalyticsSingleton for an
analytics detection). Its `alarm_end_ts` is always set — the projected end every renewal pushes
forward — so an alarm is **running** while that is in the future, and there is no such thing as one
left open by a crash.

- The operator's map polls `GET api/fs/alarms` every 3 s while it is open: alarms running now and
  those that ended in the last 15 minutes, asked of every VmsFs by end (`ending_after`) rather than
  by start, so one that started an hour ago and is still being renewed is found. web_vms keeps no
  subscriptions, so the map asks; the request is one small fan-out.
- Scoped by `ViewLive`: that a camera is alarming is part of its live state, like the boxes over its
  picture. Each alarm carries `archive` — whether the caller may also watch its recording.
- A running alarm puts a breathing red ring on the camera, wherever it is drawn (where it stands and
  its pictures alike); one that ended lately, a faint ring. It also lights every space it is drawn
  in **and every space those are nested in**: the outline of a building on the site map burns when
  a camera on any of its floors alarms, and the floor switcher and the breadcrumbs show a dot.
- The list beside the map starts with the alarms; picking one opens the space the camera is drawn
  in (this one if it is here, otherwise where it really stands) and selects it. The camera's panel
  then says since when and why, and offers the recording of the alarm beside the live picture.

## Ownership with Square

A camera whose reference placement is in a space with real coordinates owns its position in Square:
its point and heading go up with every push. Any other camera — not placed, placed only as pictures,
or standing on a plan in made-up coordinates — leaves its position to Square: it gets a default point
once, when Square first learns of it, and afterwards only its name and properties are sent.

## Next stages

- **PTZ fit by mount orientation.** The two straight lines hold only for a level mount, angular
  units and an exact camera position. A camera on a wall bracket leans; one on a fence may hang on
  its side, so that pan sweeps up and down and tilt across; units may not be linear in angle; a
  placement a metre off skews near targets. Instead, staged by the number of observations:
  - 2–3: the two lines, as now — enough to aim at all.
  - 4+: a physical model fitted in one go by nonlinear least squares (Levenberg–Marquardt, with a
    loss that tolerates an outlier): the mount's **full** 3-D orientation — any rotation, not a
    small tilt, so sideways and upside-down mounts are the same case — plus a scale and an offset
    per axis in the camera's own units. Kabsch–Umeyama on the observed directions gives the
    starting rotation. (Kabsch alone was tried elsewhere and worked badly: it needs the units to be
    angles already, and an unknown scale turns into a wrong rotation.)
  - 6–8+: a smooth correction of the remaining misses across the covered sector, for units that
    are not linear in angle — trusted only where there are observations.
  - Optionally the camera's position too, when there are many well-spread landmarks.
  A richer model is chosen only when its held-out misses are actually smaller, so extra parameters
  do not fit noise. Decide which stages are needed from 8–10 observations on a real camera that
  takes absolute positions — none is on the bench yet.
- **Master/slave through coordinates.** A new binding method `geo` aims a slave at the world point
  under the master's detection — the master's ground homography gives the point, the slave's PTZ
  fit the pan/tilt — when the two cameras' reference spaces share geometry. Needs a spatial-model
  cache in the aiming service, invalidated by `space-config-changed`.
- **Aerial targets**: a master's full pose (PnP from landmarks with heights) gives a ray; a
  co-located slave follows its direction, two masters triangulate a 3D point.
- **Export to Square**: each space a diagram (`background_img`, its footprint the `geometry` on the
  parent), each reference placement the camera object, each picture an object with `owner_id` set
  to the camera.
- A footprint's colour from the state of the cameras inside it; walls that cut view cones; zoom by
  distance.
