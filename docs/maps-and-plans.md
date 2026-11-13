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
  leaves the part; removing the camera takes the part and every picture of it.
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

- **VmsMap** owns the tables (`dotnet/VmsMap/Grpc/migrations/0002_spaces.sql`,
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

## Events

- Every change on the map — a placement, a camera's observations or marks, a space, its plan or a
  landmark — is announced on **`map-config-changed`**, payload
  `{"object_ids": [...], "camera_ids": [...]}`: the nodes that changed, and the cameras whose
  spatial model did. An empty `camera_ids` means a space or a landmark moved, which may underlie
  any camera's calibration, so every cached model is dropped; removing a space names the cameras
  that stood in it or in anything nested in it. Addressing only, like every event here. It has its
  own topic because placement edits concern no node: `server-config-changed` with an empty camera
  list would make every media server, VmsFs and VmsOnvif reconcile for nothing.

## Cameras that report themselves

A camera says more about itself than its position: it reports the live state of its relay outputs
and digital inputs, and where a mounted device's camera stands. Both are **property events**, not
getters — ONVIF's `GetRelayOutputs`/`GetDigitalInputs` return configuration (mode, idle state), and
there is no `GetRelayState` at all.

- **Events.** `Device/Trigger/Relay` and `Device/Trigger/DigitalInput` carry `Source/*Token` and
  `Data/LogicalState`. On subscription the camera replays the current state as
  `PropertyOperation="Initialized"` — that is the initial value, the thing a getter would have
  given — and later changes arrive as `"Changed"`. VmsOnvif's `DeviceIoStateService` opens a
  topic-filtered pull point for any camera whose relay or input list is non-empty, keeps the last
  state per `(camera, kind, token)`, and closes the subscription once both lists are empty again.
  It is separate from the optional alarm/analytics subscription (`onvif_events_enabled`): I/O state
  is part of the integration, not an opt-in.
- **`live_active`.** `GetRelayOutputs`/`GetDigitalInputs` fill it from that cache, and the field is
  absent until the camera has reported the device — "unknown" is not "inactive". The web shows it
  as each relay's reported state beside the command switch, and as the sensor's own `1`/`0`.
- **Polling is ours, not the camera's.** The camera pushes; the pages read VmsOnvif's cache every
  five seconds while they are visible (`DeviceIoSection`, the wall tile's indicator, the demo
  feeder). A settings read never waits on the camera for a live value.
- **`geo_from_device`.** A camera can be told that its place on the map is its own ONVIF position.
  Off by default, because a hand-placed camera is calibrated and aimed by survey and must not drift
  with a coordinate the device happens to report. While on, `CameraGeoFollower` reads
  `GetCameraGeoLocation` every five seconds and moves **only the point** of the reference replica —
  never the canonical node, the parent, height, heading or field of view — and only when it moved more than
  half a metre, publishing `map-config-changed` with both the node and the camera id so the
  browser reloads placements instead of waiting for its 30-second poll. Geographic calibration
  (ground marks, PTZ landmark observations) and aiming (a point on the map, a GEO master/slave
  pair) refuse such a camera: a fit describes the point it has left. Self-guidance is unaffected —
  it works on the camera's own picture and uses no place.
- **Wires are the simulator's, not ta_vms's.** In OnvifSim a relay's contact can be wired to the
  digital inputs of any camera and to lamps of the world (an input reads the inversion of its
  pull-up; a lamp follows its NO/NC rule). ta_vms shows only the two ends: a relay is a camera's
  output, an input is a camera's input, and what changes one appears on the other through the
  events above.

## Control surface

The operator's **Control** tab (`/operator/control`) lists the objects an operator acts on in a
space and keeps their actions beside the selected one: `GET api/control/objects?spaceId=&kind=&cameraId=&pageSize=&afterName=&afterId=`,
backed by `MapService.ListControlObjects`. Three kinds share one row, and each answers with what
its own action writes:

- **camera** (`part_kind='device'`) — arming answers the same scheme as a sensor: its own mode in
  VmsCfg (`camera_states.arm_mode`, `inherit` by default), else the zone above its reference
  replica, else off. An operator's toggle (`PUT api/cameras/{id}/state`) is an explicit command;
  who decides — the zone or the card — is the administrator's setting (`PUT
  api/cameras/{id}/arm-mode`), and `inherit` hands the flag back to the zone.
- **digital input** — the effective arming of `EffectiveArmingResolver`, its configured mode and
  alarm flag, and the contact the camera reports (`SENSOR_OPEN`/`SENSOR_CLOSED` on the part node).
- **relay** — never armed: a relay is driven, not watched. Its action is its state
  (`POST api/DeviceIo/.../relay-outputs/{token}/state`), and its row shows what the camera reports
  (`RELAY_ACTIVE`/`RELAY_INACTIVE`).

**A container is guarded as a whole.** A space — or any node a camera's reference replica actually
stands through — has a row in `object_arming`, written by `SetNodeArming`
(`PUT api/control/objects/{id}/arming`, modes `armed`/`disarmed`/`inherit`). The row is the node's
own decision (`armed` boolean); `inherit` is its absence. It is the map's typed state, not one of an
object's free-form properties: a property was editable on the object's form, without the journal
entry and the alarm re-check an arming change owes, and the migration
(`0002_object_arming.sql`) moved every `properties.armed` row into it. The zone is **not**
written onto the sensors: each one resolves it, and an alarm names the zone that settled its device
(`armingSourceObjectId`). A container nothing stands through — a diagram of a camera's parts, a
decorative figure, the server node — is not armable, because a property there would be read by
nobody. Arming a container writes the row, the journal entry (`arming`, with the operator) and
an outbox row in one transaction; the outbox row is what makes `SensorAlarmService` re-check the
sensors already in their trigger position, so a crash between the commit and the re-check loses
nothing. Disarming closes no alarm — only the condition leaving does.

**The derived flag is written back to VmsCfg.** `camera_states.armed` is read by services that
cannot see the map — VmsOnvif's ONVIF retention gate and the routing gateway — so for a camera on
`inherit` the map writes the value its zone implies: on a zone's change, when the camera's reference
replica moves or goes, when a node is moved in the tree, when the camera's own state changes, and in
a sweep at startup over the zones that are set. The write lands only while the mode is still
`inherit` and only when the value differs (`UPDATE … WHERE arm_mode='inherit' AND armed IS DISTINCT
FROM …`), so an explicit decision is never overwritten and the event a write announces cannot feed
itself.

**One arming scheme.** `EffectiveArmingResolver` answers what a sensor resolves to, and where the
answer came from: the part's own `arm_mode` (`armed`/`disarmed` → `explicit`), otherwise the nearest
`object_arming` row above the camera's **reference replica** (`space`), otherwise the camera's own
effective flag (`camera`). For a camera the walk ends at its own mode → zone → off: its flag is
what the walk produces, so reading it back as an input would turn the derivation into a loop.
`inherit` is the administrator's mode and is not an operator's command.
For a container itself the walk starts at its own node (`explicit` / `inherited` / `none`), which is
what the browser paints differently: green for armed here, purple for armed through an ancestor,
grey for not armed. The scheme is the same for one card and for a page of the panel — the page
resolves in one pass, with the settings and the camera flags read in bulk.

**The list is server-side.** It is the tree's own read, not a browser enumeration:

- `space_id` covers the space **and everything under it**, which is the set the space's own `armed`
  property reaches; an empty id asks for everything the caller may see. A zone will arrive as a
  polygon in the same request, not as a second list.
- `kind` narrows to `camera`, `digital_input` or `relay`; `camera_id` answers one device's objects
  wherever they stand — the administrator's placement palette asks this way.
- Each row is addressed by `source_kind`/`source_id`/`part_kind`/`part_token`, never by camera:
  a fire panel and its loops will join the same list under a kind of their own.
- Rows are filtered by the caller's `view_live` before the page is cut, and `can_control` says
  whether an action may be used. The page is a keyset one — ordered by the object's name, `has_more`
  plus a cursor, no total — because rights can hide rows and an offset would shift the list every
  time somebody arms something.
- A camera with no reference placement stands nowhere: a space filter leaves it out, the
  unfiltered list still shows it.
- No write path is added. The actions are the commands that already write — the camera flag, the
  sensor's `SetDeviceArmMode`, the relay's state.

**One object by any of its nodes.** `GET api/control/objects/{objectId}` answers the same row for a
canonical node or a replica of it, resolving the replica through `objects.owner_id`. The operator
map's inspector uses it, so selecting a sensor or a relay — a picture, a diagram region, or the node
itself — offers the same actions as the panel. A node the caller may not see answers not-found,
exactly as if it did not exist. A camera needs no such section there: its card arms it already, and
a second switch would be two ways to say one thing.

**Grouped by kind, and a relay is switched where it is listed.** In "All" the list is not one
heap: cameras, sensors and relays are sections of their own, because a reader scans one kind at a
time. A relay carries its switch in the list row as well as in the pane, with the state beside it;
the panel re-reads its pages every few seconds, so a change the panel did not make — another
operator, a rule — appears without an action.

**A relay command is a wish until the camera confirms it.** ONVIF has no getter for a relay's state
and a camera may report the change only when its event subscription next delivers one, a minute on
some devices. VmsOnvif therefore keeps the commanded value apart from the camera's report and shows
the wish until the camera says the same state; a report repeating an older value while the wish
stands is not a transition and does not turn the switch back. The wish is repeated until the camera
confirms it, and dropped after its lifetime (a monostable relay's `delayMs` plus a margin, otherwise
20 s) with the camera's own report back on screen and a warning in the log. The card and the ONVIF
settings pages mark a value that is still a wish; the Control list does not — VmsMap knows nothing
of the wish and shows the value the command produced.

**The tree reads a part's state, it does not drive it.** A part's row in the operator map's tree and
in Admin → Nodes carries a state dot from the same `states` the Control panel reads, and no switch:
one place drives a relay, and a second switch would be a second path that could disagree with the
first. Admin → Nodes follows the map's watch stream, so a relay switched in the panel changes there
without reopening the row.

**The card keeps its switch.** The camera card's Device I/O overlay arms a sensor on a line of its
own, under the caption "Guard", beside the effective state — not on the token line beside the live
0/1, where it read as changing what the sensor says. Arming from the monitor is ordinary operator
work; the panel is where a whole space is worked through.

## States, events and rights

- **Every camera is a node from creation.** The adapter in `VmsMap/Adapter/` creates it from
  `server-config-changed`, always under its server's node; `device_node` maps the node back to the
  camera id (and, for parts, to the part's token). A camera removed from VmsCfg takes its node,
  replicas, placements and observations with it.
- **A device's rights are anchored to its reference replica's space.** A canonical node under the
  server does not inherit a right granted on the space the camera is drawn in, so a camera node and
  its parts are also checked from the reference replica's path; with none, the server node's path is
  all there is.
- **A retention episode becomes `ALARM` plus one journal event**, and `NORM` comes back when a
  sweep confirms the episode is over; `CONNECTED`/`DISCONNECTED` follow the open source outages in
  VmsFs. The alarm counters bubble up `alarm_states` in the same transaction. The event's id is the
  episode's `event_uuid`, so a redelivered message writes nothing twice.
- **An alarm reaches the room the camera stands in.** The canonical tree ends at the server, so the
  counters alone would stop above the camera. A camera whose row changes has that row mirrored onto
  its **reference replica** — the picture on the floor plan — and the spaces above it recount; a
  picture's row is never counted as a second alarm, and only the reference replica gets one. When a
  placement moves the reference, the old picture's row goes and the new one's appears.
- **Rights are rows on tree nodes** (`rights(object_id, subject, action)`), inherited downwards;
  `admin` bypasses them, and one open/enforced switch decides what a node with no rows means.
  VmsMap computes one user's effective set and applies it to every multi-node read; web_srv's
  `RightsService` caches it per user and drops it on `rights-version`.
- **`api/map/live`** is SSE over `MapService.Watch`; the client sends a box or a set of ids, gets
  the changed ids back and fetches them over `api/map/*`. The subscription is in the request, so a
  change of view opens a new stream — Dapr proxies server-streaming but not a bidirectional call.

## Demo configuration

An empty installation shows an empty map. An optional demo world, `umd-datacenter` ("UMD Data
Center"), fills it with a fictional data centre so the product can be looked at — and checked
without a browser — right after an install.

**What it contains.** A map named `UMD Data Center (demo)` over the real campus of the University of
Maryland; the first floor of **Biomolecular Sciences** as a plan — named `First floor`, its footprint
the building's own 19-corner outline, the one OnvifSim builds its prism from, with the doorway cut in
its north wall. The floor is the room: no plan of a room is nested in it, and the hall pair stands on
its plan directly. What stands in the room is one diagram, `First floor Diagram` — a schematic of
the floor with its four racks in regions of its type, each opening on its own canvas — four racks,
every slot of every rack filled with a switch and every switch carrying its eight ports. The third
port of the first switch of the first rack is `CONNECTED`: the demo writes no
synthetic alarm, so its map starts calm. The five
OnvifSim cameras — `sim-master`, `sim-master2`, `sim-ptz`, `sim-ptz2` and `sim-vehicle` — are placed
where the world wants them; a camera the deployment does not have yet is skipped and reported, and
reinstalling once it is provisioned places it. They form two master/slave pairs, both on the same
walking person: `sim-master` → `sim-ptz`, whose master stands south of the hall on the line the
person walks out by and holds the hall and the doorway in one lens; and `sim-master2` → `sim-ptz2`
out in the parking lot, whose master, on a pole north-west of the doorway, looks back along the line
and reports the person's boxes, and whose slave stands on a pole of its own between that doorway and
the master: it holds the doorway at one side of its picture and the lot the person turns into at the
other. The car still drives the block's real ring (Regents, Paint Branch and Technology Drive), but
neither master is close enough to it to detect it. `sim-vehicle` is flagged `geo_from_device`: the
place given here is only where it starts, and the map follows the position the camera reports from
then on.

**The camera's own readings.** The `Device telemetry` type carries five regions — `lat`, `lon`,
`speed`, `relay`, `sensor`, with the picture's labels drawn from the same geometry — and a replica of
the indoor master `sim-master` stands on the floor plan beside it, showing them. It is a camera that
does stand still, which is what makes a diagram pinned to a plan honest; the one on the car has no
replica, because its place is the car on the map, which follows it (`geo_from_device`). The replica
has `objects.owner_id` set to the camera's canonical node and no `camera_placement`: a diagram's
canvas is not a space. `DemoValueFeeder` writes the values to that owner every five seconds — the
position from the camera's own `GetGeoLocation` (six decimals of a degree), the speed from two fixes
and the wall clock (km/h, one decimal; nought for a camera bolted to a wall), and the relay and
sensor states from whichever demo camera has the device (`sim-master`'s relay is wired to `sim-ptz`'s
input in the simulator's world), as `on`/`off`. A camera the map does not know yet, or one that
reports no place, writes nothing. The map answers a replica with its owner's values, so the diagram
shows the camera's readings while owning none of them; a removal takes them away with the world.

**The camera's own diagram.** The same floor carries the indoor master's device diagram: the
`Camera rear panel` template — the camera's body, lens and cable, and the terminal block its parts
are wired to — with the relay and input replicas in its four regions. It is not a world node like
the readings replica: `DemoSeeder` asks the device-diagram service for it, exactly as the
administrator's button in Admin → Maps does, so the demo ships the service's own object, placed on
the floor beside the camera under the name of the device and its template. Its marker is a thumbnail
of that same picture. Removing the demo removes it with the world and the next install makes it
again.

**The world is code.** `dotnet/VmsMap/Demo/DemoWorlds.cs` builds it from loops and constants;
`DemoWorld.cs` is the description (spaces, types and their regions, nodes, levels, camera
placements, states, rights) and `DemoSeeder.cs` applies it through the ordinary services. The
pictures ship with the code, so an install never depends on a file on the instance's disk: the floor
plan, the diagram backgrounds — the switch, the rack, the floor layout, the telemetry panel — and
the port's icon are SVG drawn by `Demo/DemoArt.cs` from the same
functions that lay the type's regions out. A socket and the region a port fills are therefore one
rectangle by construction, and a region keeps the aspect of the diagram it holds: the switch drawn in
a rack slot and the rack drawn in the floor diagram are not stretched. There is no import/export
format for maps, and the demo does not add one.

**The registry.** `demo_config` and `demo_object` (`Grpc/migrations/0006_demo_seed.sql`) record the
logical key of every row the world created — `space/building`, `object/floor-layout`,
`object/rack-a-01`, `type/rack`, `file/rack-background`, `level/rack`, `camera/sim-master` — and the
id it was created with. A
space's id is assigned by the server, so the registry is the only way to find it again. An install
upserts everything the world names under the recorded id and deletes the recorded rows the world no
longer names; a removal deletes them all, values included. **Rows the demo did not write are never
named by the registry and are never touched**: a user's own node beside the world survives both an
install and a removal.

**Installing.** Either `DEMO_CONFIG=umd-datacenter` (VmsMap installs the map, VmsCfg the cameras and
the video wall, at startup) or the button at the foot of Administration → Deployment (`POST
api/map/admin/demo/install` and
`.../remove`, `GET api/map/admin/demo`, administrators only), which calls both services and merges
their reports. Both paths use the same seeder, so they are interchangeable; installing twice is
idempotent. The whole install is one transaction under `pg_advisory_xact_lock`, so two replicas
starting with the flag cannot each write half a world.

**The demo ships calibrated.** An install also writes what the bench used to prepare: the five
OnvifSim cameras (VmsCfg, at `DEMO_ONVIFSIM_HOST`, pinned to `OWN_SERVER_ID`), the ten landmarks of
the two pairs on the floor and on the map (VmsMap), each master's ground marks and each slave's PTZ
observations — projected once from the simulator's own world and pinned with it — and the two
`method = geo` bindings with their `person` policies (VmsCfg). Without `DEMO_ONVIFSIM_HOST` the
cameras, and with them the calibration, are skipped and the report names them; the world itself
still installs. The bench (`tests/onvifsim`) is therefore a check rather than a preparation:
`provision`, `calibrate` and `bind` remain for a world of the bench's own, and `check_demo.py`
compares what is installed with the world's projection.

A diagram that stands on a plan is an ordinary point figure, not geometry of its own: in Admin →
Maps, selecting it shows a draggable handle, and "+ diagram" in the node list places a new one at the
centre of the current view. The floor's diagram is such a node; its icon on the floor is its
`visual.image`.

**The video wall** belongs to VmsCfg: a wall named `UMD DC`, layout `3x3`, showing the five demo
cameras the deployment has — a row per master/slave pair, the hall's first and the yard's second, and
the camera on the car alone on the last row. It is installed and removed with the world, and a camera
that does not exist yet is simply absent from it. The two halves are independent: the wall is built from cameras, so when
VmsCfg is unreachable the map's world is still installed and the report says what the wall failed
on, rather than failing the whole install.

**Rights.** A fresh installation starts with rights enforced (see the migration that creates the
switch); the world grants `view` on its map to the realm role `user`, so an operator sees it without
an administrator granting anything by hand. `admin` bypasses rights in any case.

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

## Calibrating a PTZ against landmarks

1. Place the camera where it really stands (its reference placement) and set its height above the
   floor.
2. Drop landmarks in the same space at points the camera can see — corners, poles, hatches — with
   their height above the floor.
3. In the camera's panel, *Calibrate by landmarks* (offered on the reference placement only): turn the camera until a landmark sits in the
   crosshair and record it. Two landmarks in clearly different directions make a calibration; a
   third lets each be checked (the "Check" column is the leave-one-out miss — redo the largest).
   While the dialog is open the camera is held for it alone (`api/ptz-tracking/{id}/hold`): neither
   a slave session nor the camera's own self-guidance moves it, including a session that starts
   meanwhile.
4. Picking another landmark, in the table or on the map, turns the camera to it. Once there is a
   calibration that is the calibration alone, with the zoom of the zoom samples, and it is also
   the test that the camera obeys absolute positions at all. Before that, it is back to where the
   landmark was recorded, if it was. Clicking the landmark already picked does not move the
   camera, so a position centred by hand is not lost on the way to *Record*.
5. For a camera that will be a slave, optionally: on a recorded landmark, zoom until a person
   standing there would fill the picture the way the slave should show them, and *Remember zoom*.
   This is a zoom sample for that landmark's distance, see [Master/slave through the
   map](#masterslave-through-the-map). It is kept apart from pan and tilt, which are best recorded
   zoomed right in, and re-recording pan and tilt keeps it.

What the fit assumes: a level mount, so pan follows the bearing and tilt the elevation, each by a
straight line in the camera's own units. A camera hung out of level shows up as a pattern of misses
and should be levelled; one mounted on its side or at an angle cannot be calibrated by this fit at
all (see "PTZ fit by mount orientation" under the next stages). The camera's pan seam, where its
counting wraps round, may fall anywhere, among the landmarks too: the fit finds it, given three
landmarks or more (see [Algorithms](#algorithms)).
Cameras whose `GetStatus` is a stub are refused with a message — they can still be aimed by presets
through the slave binding. What the fit computes is under [Algorithms](#algorithms).

**Mounts and the way cameras count their axes.** ONVIF's generic position space says nothing about
direction: one camera's pan grows clockwise, another's anticlockwise; tilt may count up or down; the
units may be -1..1 or degrees. A camera hung upside down is usually set by its installer to turn its
picture back (the video source's `Rotate`) and to count both axes the other way (PTZ
`PTControlDirection` `Reverse`), but not always, and not always both. None of that needs a setting
here: each of the two lines takes its axis' sign and scale from the landmarks, so a reversed axis, a
camera upside down with or without its picture turned and with or without `Reverse`, and degrees
instead of -1..1 all calibrate the same way. ta_vms reads neither `Rotate` nor `Reverse`, and does
not need to. What the lines cannot take is a pan axis that is not vertical — a bracket that leans, a
camera on its side ("on a fence", where pan sweeps up and down and tilt across). A fixed camera's
ground mapping takes its picture as it comes, turned or tall; but its marks and the master's aim
point are the bottom of a person's box, which in a picture left upside down is their head. The bench
in `tests/onvifsim` hangs a pair each of these ways (`onvifsim_lab.py matrix`); its README has what
each does to the aim.

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
  (`GroundHomography.FloorElevationM`). The slave therefore looks at feet.
- **Zoom by distance, from samples.** The slave zooms by the zoom samples in its landmark
  calibration (`Geo/ZoomByDistance`). Each sample is the zoom the operator chose at one landmark,
  stored with the observation as `zoom`; its distance is not stored but follows from where the
  camera and the landmark stand. Between samples the zoom is interpolated in ln(distance). An
  object keeps its size when the focal length grows with its distance, and on a lens whose zoom
  steps are equal ratios that makes zoom a straight line in ln(distance). Nothing is assumed about
  the lens beyond that: ONVIF reports zoom in the camera's own units, whose mapping to a field of
  view differs between models, so a sample says directly what to send.
  - Beyond the nearest and the farthest sample the zoom stays at theirs, so the samples are also
    the range the slave may use.
  - An aim outside the landmarks' sector (*extrapolated*) uses the widest sample, and so does a slave
    whose target is lost: it keeps pointing where the target was last seen but zooms out, so the
    next object nearby is in the picture.
  - With no samples the slave is sent to zoom 0, the wide end. An absolute move always carries a
    zoom, so "leave it as it is" is not something the slave can ask for.
- **Blocked with a reason.** The pair does not run, and the reason shows in the binding's
  `unusable` and the tracking state, when any of these holds:
  - either camera has no reference placement;
  - the two reference spaces share no geometry;
  - the master has no ground fit;
  - the slave has no landmark fit.
- **Cached models.** The aiming service holds both cameras' spatial models in `SpatialModelCache`.
  `map-config-changed` marks the cameras it names stale, and an empty list marks all of them stale.
  There is a one-minute TTL as a backstop. Like every cache in the service
  (`TtlCache`), a stale entry is kept until a read replaces it. A failed read keeps the camera
  aiming by what it had, and is reported as "could not be read", never as "not calibrated".
- **One object per slave.** `SlaveAim` is the binding plus, on this method, both models. It
  answers "can this aim" and "where does this spot send the camera" for every method. The reconcile
  replaces it whole, so a detection is never aimed by a new binding through old calibrations.
  Changing the master restarts the slave's session, since detections are routed by master.
- **Same law as a survey, except the dead zone of a slave that zooms.** The command interval and
  target choice are those of every slave (`SlaveAimingSession`). A slave with zoom samples measures
  its dead zone on the ground rather than in 0.02 of the head's units: a zoomed frame can be
  narrower than 0.02 of pan, which is 3.6° on a head with a full turn, while a frame sampled to fit
  a person is a stretch of ground at any distance. The threshold is 0.5 m, raised to what the
  master's box jitter (0.4% of its frame height) amounts to on the ground at the object, which grows
  with the square of the distance from the master. A change of zoom alone of 0.02 also counts. A
  slave without samples keeps 0.02 of the head's units. A changed binding, placement, observation,
  mark or zoom sample reopens the dead zone; the same calibrations merely read again do not.
- **Checking it.** The binding's tab shows both pictures and no survey. A click on the master's
  ground sends the slave there through both calibrations. The tab re-reads the binding's status
  when it comes back into view, since what is missing is usually fixed on Maps & plans.

It was proved on a virtual bench, [`tests/onvifsim`](../tests/onvifsim/README.md). In the OnvifSim
world that bench builds, the head arrives within about a quarter of a degree of the person it was
sent to. With zoom samples framing 8 m at each landmark, the whole person stays in the slave's
picture in over 99% of samples and fills a median 38% of its height, against under 10% at the
wide end. The bench
runs a second pair over the same yard on a plan in made-up coordinates, whose picture is the yard
photographed from above, and checks there that a master on the map is refused for a slave on the
plan.

## Algorithms

What the calibrations and the aiming compute, for a reader who wants to know what is exact and
what is a model. Everything is in `dotnet/VmsPtzTrackingSingleton/Geo` unless said otherwise.

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

    pan  = a₁ + b₁ · ((azimuth − seam) mod 360)
    tilt = a₂ + b₂ · elevation

`seam` is where the camera's pan wraps round. A head that pans endlessly reports a jump of its whole
range there, wherever the installer happened to point it, so it is found rather than assumed: it
lies in one of the gaps between neighbouring landmarks (round the circle), and only in that gap do
the readings make one straight line — in any other the jump is a step no line fits. Each gap is
tried, its middle taken as the seam, and the least-squares line that misses least wins; that needs
no knowledge of how long a turn is in the camera's units. Two landmarks fit either way round, and
then the seam is put in the wider gap, behind them. A target between the middle of the seam's gap
and the true seam then comes out past the end of the camera's range, which a camera that checks its
range refuses (OnvifSim does). In ONVIF's generic space −1..1 is the whole range by definition, so
when the slope makes a turn 2 there — a head that pans round — such an aim is folded back by a turn;
a camera counting in other units is left as it is. The slopes absorb whatever units the camera
reports in and the sign of its mount. The fit needs two landmarks at least 5° apart in azimuth and
1° in elevation, and readings that actually move between them — a camera whose `GetStatus` is a stub
is refused on that. *Extrapolated* means the target's azimuth or elevation falls outside the range
the landmarks cover, with 1° of slack; in azimuth that is the circle less its widest gap, so a
sector that straddles north is one sector. The "Check" column is leave-one-out: the fit without that
landmark, evaluated at its bearing, as a miss in pan and one in tilt, in the camera's own units;
with only two landmarks there is nothing to leave out. The seam stays where all the landmarks put it
when one is left out. What the lines do not know: a mount that is not level, which shows as a
pattern of misses rather than a few bad ones (see "PTZ fit by mount orientation" under the next
stages).

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
that point on the master's floor → the slave's two lines → a pan and tilt in the slave's units,
and the distance of that point → the zoom samples → a zoom. The command interval is the session's,
the same as for a survey; the dead zone is measured on the ground.

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

- M3 makes the map's copy of an alarm a state on the camera node (see "States, events and
  rights"), but the operator's screen keeps polling `GET api/fs/alarms` until M4 moves it onto
  `api/map/live`.
- The operator's map polls `GET api/fs/alarms` every 3 s while it is open: alarms running now and
  those that ended in the last 15 minutes, asked of every VmsFs by end (`ending_after`) rather than
  by start, so one that started an hour ago and is still being renewed is found. web_srv keeps no
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
  takes absolute positions — none is on the bench yet. What the two lines cost today is measured
  on OnvifSim (`tests/onvifsim/README.md`, "What the mounts do to the aim"): a bracket leaning 10°
  forward stays within the thresholds, 8° sideways already misses them (1.7° mean, 2.7° p95), and
  a camera on its side is unusable (13° off, framed 12% of the time). Those two mounts are the
  test the new model has to pass.
- **Self-guidance over the mounts.** The bench measures only aiming through the map
  (`AbsoluteMove`). A camera that follows a person in its own picture is a different loop: the
  box's offset from the centre, times a 2×2 matrix, becomes a `ContinuousMove` velocity
  (`PtzTrackingMath`), and the matrix is measured in the picture by test pulses
  (`PtzCalibrationService`). That absorbs a reversed or swapped axis and a picture turned by a
  fixed angle by construction. What it cannot absorb is a mapping that changes along the travel —
  a leaning mount or one on its side, where the picture turns as the head pans, and zoom, which
  changes the gain — because the matrix is measured at one pose. And with no usable calibration
  (`|det| < 0.05`) it falls back to `(errorX, -errorY)`, the standard way round: on a camera with a
  reversed axis, or upside down with its picture left as it is, that is positive feedback and the
  head runs away from the person. Nothing checks either today. A bench step for it: give the
  slave its own analytics, run the automatic calibration (first finding out whether its motion
  estimate works on OnvifSim's flat-coloured surfaces), switch self-guidance on while a person
  walks, and measure the share of time in frame, the mean offset from the centre, overshoot and
  oscillation, and losses of the target — over the whole mount matrix, plus each mount with no
  calibration at all. If the uncalibrated fallback runs away, it should refuse to guide rather
  than guess.
- **A master whose picture is upside down.** The ground mapping takes a picture turned any way,
  but the aim point is the bottom of a person's box, which in a picture left upside down is the
  head: with marks clicked on the landmarks' feet the slave is then 27° off and never frames the
  person (bench mount `master-flip`). The fix belongs in the camera — turn its picture back — so
  the first step is to say so where the operator marks the ground, since nothing in the marks
  themselves gives the flip away. Teach the aim to use the box's top edge only if a camera turns
  up whose picture cannot be turned back.
- **A dead zone in degrees for a survey.** A survey slave's dead zone is 0.02 in the camera's own
  units. On a head whose generic range is a full turn, that is 3.6° of pan. That threshold, not the
  calibration, is what dominates how far behind a walking person it lags. (On the map method it is
  already measured on the ground.)
- **Aerial targets**: a master's full pose (PnP from landmarks with heights) gives a ray; a
  co-located slave follows its direction, two masters triangulate a 3D point.
- A footprint's colour from the state of the cameras inside it; walls that cut view cones.
