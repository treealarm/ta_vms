# Maps & plans

ta_vms places cameras and reference points in space on its own. An administrator
lays out maps and plans under **Admin → Maps**; an operator watches cameras where they stand under
**Operator → Map**, dives into nested plans, and can point a calibrated PTZ at a place by clicking it.

The same positions are what lets the VMS aim cameras and place what they see: a PTZ calibrated
against landmarks turns to any point that shares its coordinates, a fixed camera marked against
landmarks shows its live detections on the map, and (next stage) a slave follows a master's objects
through those coordinates instead of a hand-marked pixel survey.

## Model

The shape is Square's diagrams: a tree of worlds, each drawn on its own background, each nested
one reachable from its outline on the parent. Everything below lives in VmsMap's database, on its
one object tree: a space and a landmark are nodes of `objects` with side rows of their own.

| Entity | Table | Meaning |
|---|---|---|
| Space | `spaces` | One world things are placed in; a node of `objects` with a row of its own. `map`: a root over map tiles, real coordinates. `plan`: a root or a nested space drawn on its own picture, in real or made-up coordinates. Has a default view, an order among siblings, a floor elevation and — when nested — a footprint polygon on its parent. |
| Plan | `space_plan` | A plan space's picture and three of its corners in that space (top-left, top-right, bottom-left). |
| Placement | `camera_placement` | Where a camera is drawn in one space: lat/lon, height, heading, view angle, range. One per camera and space; at most one per camera is its **reference**. |
| Landmark | `landmark` | A named point with a known position and height in a space, used only to calibrate cameras. A node of `objects` too. |
| Observation | `ptz_geo_observation` | Where a PTZ pointed (its own pan/tilt units) when centred on a landmark. |
| Ground mark | `ground_observation` | Where a landmark stands on the ground in a fixed camera's picture, normalised 0..1 like detection boxes. |

Conventions that hold everywhere:

- **Spaces nest, coordinates do not.** A nested plan is never laid over its parent's map: its
  footprint on the parent is only the way in. The floors of a building are sibling spaces; the
  switcher on the map's edge moves between them, breadcrumbs move up, footprints move down. When
  footprints overlap (floors sharing one outline), a click asks which one.
- **A canvas is the unit of drawing, not a level of the tree.** A space or a mnemoscheme draws
  every figure whose `geo_objects.canvas_id` is that canvas — the nearest space or diagram above
  the figure — whatever the depth of the tree between them. So a **container** (a polygon, a circle, an icon, a node with no figure
  at all) can hold children, and those children are drawn on the same canvas as the container, not
  only after entering it. A nested space is a canvas of its own: its footprint is drawn on the
  parent's canvas, its children on its own.
- **A canvas larger than one read is read in pages.** A read is capped at `count` (10 000 by
  default) and says `truncated` when it stopped short; `next_cursor` names the id it ended at, and
  the next read passes it back as `after_id` to continue strictly after it in the same order by id,
  so the pages add up to the canvas with nothing repeated or skipped. The cursor is the read's
  bookmark, not the last figure of the answer: rights filter after the cut, so a page may end on a
  figure the caller cannot see. A clustered rectangle answer has no cursor — its centroids are an
  aggregate of the read, not its prefix — and the UI does not page a canvas: it narrows the space
  instead, and says so beside a cut one.
- **A diagram carries free objects as well as its typed children.** Text, lines, polygons and icons
  laid on a mnemoscheme are ordinary figures whose `canvas_id` is the diagram; their coordinates
  are that canvas's pixels, carried in the geometry's lon/lat slots, the same way a made-up plan
  carries meaningless WGS84 around (0, 0). They are edited like a plan's shapes — the same node
  editor, the same drag — and drawn under the typed children (lines and zones) or over them
  (points, captions, icons). A diagram is therefore an inventory of a place, not only a picture of
  typed slots.
- **A type can be the template of a real device.** Besides the picture and the regions, a type may
  name a device type (`source_kind`, `camera` today), the manufacturer and model it describes
  (empty is any) and whether it matches a device's model by itself. Each of its regions may then
  stand for one part of that device — a digital input or a relay, found by its token, the name the
  operator gave it, or its position among parts of its kind. A device's diagram is a map object:
  **the administrator draws it on a canvas** (the device's row in Admin → Maps), the service puts it
  beside the camera's placement, takes the type whose manufacturer and model the device reports —
  or a blank sheet when none does — and hangs the replicas under it. The tree under the server stays
  hardware: a logical object does not live there. Only parts the device declares *and* the operator
  configured get a replica; a region with no such part stays as the template drew it. The service
  keeps the replicas it made in `device_diagram_parts`, so one an administrator drew by hand is
  never moved or removed. A diagram placed on a map has no stored paper size: the viewer lays it out
  on the size of its type's picture, so regions, stored in shares, land where the template draws
  them.
- **The tree decides what is a child; geometry never does.** An object inside a polygon is not its
  child unless the tree says so, and a child need not lie inside anything. The administrator sets
  the parent: a new object is created under the selected node, or under the space when nothing is
  selected, and an existing one is moved with *Move to…*. A move under the node's own descendant is
  refused. A camera's or a landmark's place on a canvas is read from `geo_objects.canvas_id`, not
  from its parent — which is what lets it hang under a container and still be placed on the space.
- **A scale layer (`levels`) belongs to any node with a shape.** The dictionary names the map's
  scales the way OSM cuts its tiles — `World` 0–3, `Continent` 4–5, `Country` 6–7, `Region` 8–9,
  `City` 10–12, `District` 13–14, `Street` 15–16, `Building` 17–18, `Room` 19–22 — and two layers
  may share a zoom: a gate lies on its street and in the building it is set in, and a node is given
  one layer of its own. A node on a layer is drawn while the canvas's zoom is inside the layer's
  range; a node without one is always drawn, and so is one whose layer has been removed from the
  dictionary. Layers are a map's tool — the map editor offers the field on maps only, since a plan
  is looked at at one scale. *Enter* on a container raises the zoom to the lowest layer among its
  children and centres on it — on its own shape, or on its children when it has none — rather than
  fitting its polygon, which may be a circle, an icon or nothing. A space or a mnemoscheme is
  entered by opening its own canvas.
- **Real or made-up coordinates.** A map, and any plan marked real, is in WGS84. A plan in made-up
  coordinates still stores WGS84, made up around (0, 0), so one code path serves every space — but
  its numbers mean nothing anywhere else.
- **Two spaces share geometry when they are the same space or both are real.** That rule decides
  everything that crosses spaces: which landmarks may calibrate a camera, which points a camera can
  be aimed at, and (stage 3) which masters can drive which slaves. VmsCfg (`SpatialDb`), the
  tracking service (`CameraSpatialModel.SharesGeometryWith`) and the client (`sharesGeometry`)
  apply the same rule.
- **A camera has one reference placement and any number of pictures.** The reference is where it
  really stands, and aiming and calibration go by it; it belongs to a replica of the camera, not to
  the camera's canonical node. Pictures — the camera drawn again on a detailed plan of its corner —
  are replicas too, each with its own point and placement row, but no states of their own. A
  camera's first placement becomes its reference; clearing the flag leaves the camera with none,
  never quietly promoting another.
- **A device is a canonical node; everything else is a replica of it.** Three words, one rule each:
  a **camera** is the device — its canonical node, id derived from the camera id, standing under its
  server's node, holding the meaning (states, events, alarms, rights) and no picture at all; a
  **picture** is a replica of it standing on a map or a plan, with a placement row of its own — one
  of which, `is_reference`, says where the camera really stands; a **representation** is a replica
  standing on a diagram, with no placement row at all. Bindings — the reference placement,
  calibration, aiming, master/slave — are read by camera id, from the camera's reference replica,
  and only in a space with real coordinates: a diagram has no geographic frame, so nothing placed
  on it can be bound. A camera placed only on a diagram has no reference until it is put on a map
  or a plan. A replica shows its owner's state and has none of its own; how it looks is its own —
  one camera may look different on a map, a plan and a diagram. Which of the two a node is, is said
  by `objects.owner_id` alone, as it was in Square: empty means the node is the object itself — an
  abstract node, a container, a place or a device — and set means a replica of the node it names.
  No separate "is a device" flag is kept; `device_node` says which originals are devices, and which
  part of one (`part_kind`, `part_token`). `owner_id` always names an original, never another
  replica: a picture dropped from a replica's canvas is written as a picture of the node both show,
  because every read that answers through an owner resolves it in one hop. **A device need not be
  placed at all**: it exists as a canonical node the moment it is configured, and its parts —
  digital inputs and relays — are canonical nodes under it. That is the tree **Admin → Nodes**
  shows in full and **Admin → Maps → Devices** offers for placement; the map's **Nodes** tab shows
  only what is drawn on the open space, pictures included. **A part may be drawn too.** A digital
  input or a relay placed from the palette is an ordinary replica of the part's node: a point on
  the space, a name and a colour of its own, no placement row and no camera binding. It shows what
  the owner reports and offers the same actions as the Control panel; it counts no alarm of its
  own, because the room counts the alarm of the camera it stands in, once. Removing the picture
  leaves the part; removing the camera takes the part and every picture of it. **A replica is named
  after its owner and its own number** — `cam1 rep1`, `relay4 rep1` — and the server picks the
  number, so no client can guess one another has taken; a name the caller gives is kept as it is.
  A world that writes its own replicas writes their names too.
- **Positions are WGS84 plus a height in metres above the floor of their space** — never above sea
  level. Height above the ground is `space.elevation_m + height_m`; elevation matters only where
  coordinates are real, so a camera upstairs and one in the yard agree on heights.
- **A space opens on its default view** (`view_*`: south-west and north-east corners), fitted into
  whatever window shows it, so every screen starts at the same level of detail. A new map is framed
  in its creation form; "Open the space on this view" in the editor reframes any space.
- **Nothing is stored in plan pixels.** Re-aligning a plan moves the picture, not the objects.
- **An abstract property carries a type, not just text.** `properties.visual_type` says how its
  value is read and edited: empty or `__txt` is plain text — the default — `__clr` a colour,
  `__datetime` an instant, `__double` a number and `__int` an integer. The editor stores numbers in
  one invariant spelling and instants as ISO 8601 UTC; a value that does not parse is kept as typed.
  Square's action-parameter types (password, coordinates, IP range, credential and enum lists,
  snapshot, map) went with the actions and are not carried over. The type names in the editor are
  localized; the stored strings are not.
- **Heading is a compass bearing**: 0 = north, clockwise — the same as Square's `__image_rotate`.
- **Fits are never stored.** VmsMap keeps raw observations and marks; `VmsPtzTrackingSingleton`
  fits them on demand, exactly as it does the slave binding.
- Geometry is measured in an equirectangular frame centred on the camera (`Geo/EnuFrame.cs`):
  millimetres of error across a few hundred metres, about a metre at 2 km.

## Where things live

- **VmsMap** owns the tables (`dotnet/VmsMap/Grpc/migrations/0000_init.sql`,
  `Db/Services/SpatialDb.cs`) and the gRPC contract (`proto/vms_map.proto`, section
  "Maps & plans"). Plan images are `bytea` in its database and travel as chunked gRPC streams,
  because Dapr service invocation caps one message at 4 MiB. Limit: 20 MiB per image. A placement
  write checks the camera exists in VmsCfg through `GetCamera`: cameras stay VmsCfg's, and no
  foreign key spans the two databases.
- **VmsPtzTrackingSingleton** owns the geometry (`Geo/`): the local frame, bearings, the
  pan/tilt-against-azimuth/elevation fit with leave-one-out residuals, `AimAtWorldPoint`, and the
  picture-to-ground homography of a fixed camera (`GroundHomography`). It is the one owner of a
  camera's motor, so map aiming goes through it like every other aim; the homography lives beside
  the PTZ fit because the master/slave stage needs both in one place.
- **web_srv.Server** serves `api/spatial/*` (spaces, plan images, placements, landmarks),
  `api/ptz-geo/*` (observations, fit, aim), `api/ground-geo/*` (marks, fit, and the mappings a map
  needs), `api/map/*` (the tree, states, events, tracks, values, diagrams, rights, levels, files and
  the SSE stream) and `api/map/tiles/*`.
- **web_srv.client**: Leaflet + react-leaflet 5. `src/map/` holds the map primitives (rotated plan
  layer, authenticated tiles, metric grid, markers, footprints); `src/components/maps/` the two tabs.
  The administrator's page is three columns: on the left what there is, as tabs that are always in
  view — the spaces as a tree, every camera of the system by server (saying whether it is placed
  here, stands elsewhere or nowhere yet), the open space's landmarks; the map in the middle, with
  only the map's own tools above it; on the right the properties, the space's own on one tab and
  the selected object's on the other. What is selected, which tool is active and which camera is
  waiting to be placed is shared between the lists and the map (`EditorStateProvider`), so a list
  never gives way to a property panel.

## Map tiles

`VMS_MAP_TILE_URL` (optional, a `{z}/{x}/{y}` template) names the provider; `VMS_MAP_TILE_ATTRIBUTION`
the credit line it requires. Browsers never call the provider: web_srv proxies every tile and keeps
it in the Dapr state store for 30 days, shared by all replicas (web_srv keeps no local files), sends
an identifying User-Agent and runs at most two downloads at a time per process. That is what the
OpenStreetMap tile policy asks for; it still allows only light use, so a larger deployment points
the variable at its own tile server or a commercial provider. Empty means maps are drawn without
tiles. Expect a cold area to fill in over several seconds the first time it is opened.

Plans never show tiles to operators. While an administrator lines up a plan in real coordinates,
"Map underneath" shows them for that purpose only.

## Events

- Every change on the map — a placement, a camera's observations or marks, a space, its plan or a
  landmark — is announced on **`map-config-changed`**, payload
  `{"object_ids": [...], "camera_ids": [...]}`: the nodes that changed, and the cameras whose
  spatial model did. An empty `camera_ids` means a space or a landmark moved, which may underlie
  any camera's calibration, so every cached model is dropped; removing a space names the cameras
  that stood in it or in anything nested in it. Addressing only, like every event here. It has its
  own topic because placement edits concern no node: `server-config-changed` with an empty camera
  list would make every media server, VmsFs and VmsOnvif reconcile for nothing.

## The rest of this document

Each part is its own file, so a change to one of them does not need the others.

| File | What is in it |
|---|---|
| [devices.md](maps/devices.md) | cameras that report their own state (relays, inputs, the device they are mounted on), detections drawn on the map |
| [control.md](maps/control.md) | the operator's Control tab: the objects it lists and the action each kind answers with |
| [states-and-alarms.md](maps/states-and-alarms.md) | states from their owners, the journal, rights on tree nodes, alarms on the map |
| [demo.md](maps/demo.md) | the optional `umd-datacenter` demo world and how it is installed |
| [calibration.md](maps/calibration.md) | PTZ landmark calibration, fixed-camera ground calibration, master/slave aiming, and the algorithms behind them |
| [25-ptz-mounts-and-self-guidance.md](future_plans/25-ptz-mounts-and-self-guidance.md) | what the fit and the guidance loop do not cover yet |
