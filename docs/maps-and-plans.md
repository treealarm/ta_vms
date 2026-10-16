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
  event here. The aiming service subscribes (it drops every cached spatial model, since a moved
  landmark may underlie any camera's calibration), and so does the Square producer.

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
through the slave binding. What the fit computes is under [Algorithms](#algorithms).

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
there. How the mapping is solved is under [Algorithms](#algorithms).

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

## Master/slave through the map

A slave bound to its master by the method `geo` is aimed with no survey of the pair. The master's
ground calibration turns the foot of an object into a place, and the slave's landmark calibration
turns that place into a pan and tilt (`Geo/GeoAiming`). Each camera is calibrated alone, so a pair
works the moment both are, and recalibrating one camera fixes it for every pairing it is in.

- **The target is the foot.** It is the bottom middle of the box, put on the master's floor
  (`GroundHomography.FloorElevationM`). The slave therefore looks at feet. Zoom is not touched.
- **Blocked with a reason.** The pair does not run, and the reason shows in the binding's
  `unusable` and the tracking state, when any of these holds:
  - either camera has no reference placement;
  - the two reference spaces share no geometry;
  - the master has no ground fit;
  - the slave has no landmark fit.
- **Cached models.** The aiming service holds both cameras' spatial models in `SpatialModelCache`.
  `server-config-changed` marks the cameras it names stale, and `space-config-changed` marks all
  of them stale. There is a one-minute TTL as a backstop. Like every cache in the service
  (`TtlCache`), a stale entry is kept until a read replaces it. A failed read keeps the camera
  aiming by what it had, and is reported as "could not be read", never as "not calibrated".
- **One object per slave.** `SlaveAim` is the binding plus, on this method, both models. It
  answers "can this aim" and "where does this spot send the camera" for every method. The reconcile
  replaces it whole, so a detection is never aimed by a new binding through old calibrations.
  Changing the master restarts the slave's session, since detections are routed by master.
- **Same law as a survey.** The dead zone, the command interval and target choice are those of
  every slave (`SlaveAimingSession`). A recalibration does not reopen the dead zone. It compares
  positions of the head, which mean the same whatever calibration computed them.
- **Checking it.** The binding's tab shows both pictures and no survey. A click on the master's
  ground sends the slave there through both calibrations. The tab re-reads the binding's status
  when it comes back into view, since what is missing is usually fixed on Maps & plans.

It was proved on a virtual bench, [`tests/onvifsim`](../tests/onvifsim/README.md). In the OnvifSim
world that bench builds, the head arrives within about a quarter of a degree of the person it was
sent to, and the person stays in the middle third of the slave's picture throughout.

## Algorithms

What the calibrations and the aiming compute, for a reader who wants to know what is exact and
what is a model. Everything is in `web_vms/VmsPtzTrackingSingleton/Geo` unless said otherwise.

**The frame** (`EnuFrame`). Every camera has its own equirectangular east-north frame centred on
itself, on a sphere of radius R = 6 378 137 m: a metre of north is 1/(R·π/180) degrees of latitude,
and a metre of east that divided by cos(lat₀). The error is millimetres across a few hundred
metres and about a metre at 2 km, well inside what a PTZ can be aimed to. A plan in made-up
coordinates lives near (0, 0), where cos ≈ 1, so its made-up degrees behave as metres through the
same formula: there is no second coordinate system anywhere.

**A bearing** (`PtzGeoMath.BearingTo`). The earth is flat within a frame: azimuth is
atan2(ΔE, ΔN) folded into [0, 360), elevation is atan2(ΔH, horizontal distance), where each
height is the floor of the camera's space plus the object's height above that floor.

**A PTZ against landmarks** (`PtzGeoMath`). Two independent straight lines, each fitted by
ordinary least squares:

    pan  = a₁ + b₁ · wrap180(azimuth − azimuth₀)
    tilt = a₂ + b₂ · elevation

`azimuth₀` is the circular mean of the observed azimuths (atan2(Σ sin, Σ cos)), so a sector that
straddles north stays one straight run. The slopes absorb whatever units the camera reports in
and the sign of its mount. The fit needs two landmarks at least 5° apart in azimuth and 1° in
elevation, and readings that actually move between them — a camera whose `GetStatus` is a stub is
refused on that. *Extrapolated* means the target's azimuth or elevation falls outside the range
the landmarks cover, with 1° of slack. The "Check" column is leave-one-out: the fit without that
landmark, evaluated at its bearing, as a miss in pan and one in tilt, in the camera's own units;
with only two landmarks there is nothing to leave out. What the lines do not know: the camera's
own pan seam, which is never unwrapped and must not fall between the landmarks, and a mount that
is not level, which shows as a pattern of misses rather than a few bad ones (see "PTZ fit by
mount orientation" under the next stages).

**A fixed camera against the ground** (`GroundHomography`). A plane seen by a pinhole camera is a
projective transform of the picture, so the ground mapping is a homography — eight unknowns
with h₃₃ = 1, two linear rows per mark (the direct linear transform). Both point sets are
normalised first — centroid at the origin, mean distance √2 — and the normal equations of the
stacked rows are solved by Gaussian elimination with partial pivoting; a vanishing pivot is three
marks on one line. With more than four marks this is an algebraic least-squares fit. The marks
must be on one floor (within 5 cm), and spread out both in the picture and on the ground. The
sign of H is chosen so that the denominator w = h₇x + h₈y + h₉ is positive at every mark; a
point where w ≤ 0 is beyond the horizon, and projecting it answers nothing. The miss shown for each
mark is against the fit of all of them, in metres, for the reason given under the calibration
above. The browser receives the same H pre-multiplied by the frame's linear ENU→WGS84 map, a
3×3 matrix that turns (x, y, 1) into (lat·w, lon·w, w).

**A survey binding** (`PtzSlaveBindingMath`, the method `absolute`). Not a homography: the pair is
not two views of one plane, since the slave's pan and tilt are angles, not a picture. The marked
points are interpolated by a thin-plate spline — kernel U(r) = r²·ln r plus an affine part, pan and
tilt as two right-hand sides of one (n+3)×(n+3) system, regularised by λ = 10⁻³ so that a
single misplaced mark bends the sheet rather than tearing it. Three points make the sheet (an
affine map, then); *extrapolated* is outside the convex hull of the marks; the leave-one-out
miss is one Euclidean number over pan and tilt. The method `presets` fits nothing: it goes to the
preset of the nearest mark.

**The chain for `geo`** (`GeoAiming.Project`): the bottom middle of the master's box → the
master's homography → a point in the master's frame → WGS84 → the slave's frame → a bearing to
that point on the master's floor → the slave's two lines → a pan and tilt in the slave's units.
The dead zone and the command interval are the session's, the same as for a survey.

**Exact where, approximate where.** For an ideal pinhole camera over a flat floor and a level
PTZ with a linear scale, these models are exact: on the OnvifSim bench the PTZ fit's residual is
zero, the ground fit's 6 cm, and the head arrives within a thousandth of a degree of a point it
was sent to. A real camera departs from them through lens distortion (the ground mapping misses
more towards the edges of a wide frame), a mount out of level and a pan scale that is not quite
linear. The models do not correct any of that; they show it, in the residual columns, which is
why those columns are there.

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

## Export to Square

When the Square integration is on, `VmsSquareSingleton` puts the spaces into Square as its own
diagrams (`SquareSpaceExport`, run by `SpaceExportWorker` on `space-config-changed`, on a camera's
`server-config-changed` — its placements — and at the startup resync). VmsCfg knows nothing of it.

| ta_vms | Square |
|---|---|
| Space | a tree object (`ToObjectId(space id)`), under its parent space or the producer's root; integro type `space` |
| Map | a tree node only — its paper is Square's own map |
| Plan | a diagram. A root plan's paper is its picture's pixels (or its default view, 1000 px wide, with no picture). A plan in a plan sits on the box round its footprint in the parent's pixels. A plan in a map is a diagram of its own, and its footprint goes onto Square's map as a polygon — the way in |
| Plan picture | uploaded once per version (`UploadFile` into `static_files/<producer>/plans/<space>-<etag>.<ext>`), named in `background_img` |
| Reference placement | the camera object itself: it hangs under the space it stands in; on a plan it is an icon there (diagram type `vms-camera`, Square's camera picture); in real coordinates it also keeps its point and heading on the map |
| Picture placement | a replica: its own object (`ToObjectId("<camera>/<space>")`) with `owner_id` = the camera, under that space — an icon on a plan, a point on a map |

How a point gets into a diagram: Square places a diagram's children in pixels of the diagram's own
rectangle and stretches its picture over that rectangle. So a point goes from WGS84 into the plan's
pixels (the inverse of the affine map its three corners define — a plan turned on the map comes out
upright, as Square shows it) and is then stretched by the same factors as the picture.

Assumptions about Square, read from its code and **not yet tried against a running Square**:
- IntegrationHost's `UploadFile` writes under the same root LeafletAlarms serves `static_files`
  from (both mount `ROOT_DATA` in Square's compose).
- A figure push rewrites an object's place in the tree without its `owner_id`, so the export sends
  figures before the tree.
- Square has no delete in its integration protocol: a space or picture removed here stays in Square,
  as a removed camera does, until the protocol can say so.

What is checked: the geometry and the batch built from it (`VmsSquareSingleton.Tests`); the push
itself only compiles.

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
- **A dead zone in degrees.** The slave's dead zone is 0.02 in the camera's own units. On a head
  whose generic range is a full turn, that is 3.6° of pan. That threshold, not the calibration, is
  what dominates how far behind a walking person a slave lags.
- **Aerial targets**: a master's full pose (PnP from landmarks with heights) gives a ray; a
  co-located slave follows its direction, two masters triangulate a 3D point.
- **Export to Square, the rest**: delete (needs Square's protocol), landmarks and ground marks.
- A footprint's colour from the state of the cameras inside it; walls that cut view cones; zoom by
  distance.
