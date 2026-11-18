# Demo configuration

> Part of [Maps & plans](../maps-and-plans.md): the model and where things live are there.

## Demo configuration

An empty installation shows an empty map. An optional demo world, `umd-datacenter` ("UMD Data
Center"), fills it with a fictional data centre so the product can be looked at — and checked
without a browser — right after an install.

**What it contains.** A map named `map1` over the real campus of the University of
Maryland; the first floor of **Biomolecular Sciences** as a plan — named `plan1`, its footprint
the building's own 19-corner outline, the one OnvifSim builds its prism from, with the doorway cut in
its north wall. The floor is the room: no plan of a room is nested in it, and the hall pair stands on
its plan directly. What stands in the room is one diagram, `diag1` — a schematic of
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
are wired to — with the relay and input replicas in its five regions. It is not a world node like
the readings replica: `DemoSeeder` asks the device-diagram service for it, exactly as the
administrator's button in Admin → Maps does, so the demo ships the service's own object, placed on
the floor beside the camera under the name of the device and its template. Its marker is a thumbnail
of that same picture. Removing the demo removes it with the world and the next install makes it
again.

**A part on the map.** The map carries one replica the world places itself: the indoor master's
fourth relay, `relay4 rep1`. It closes the same sensor `relay3` does in the simulator, so either of
them raises what the input raises, and it stands on the map rather than in the camera's diagram
because that is where an operator switches it — the inspector resolves the picture to the relay and
offers the switch, as the Control panel does. The demo grants `control` to the `user` role on the
floor (`space/building`), which is where a device's rights are anchored; a right on the map above it
would not reach the device's node.

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

**The registry.** `demo_config` and `demo_object` (`Grpc/migrations/0000_init.sql`) record the
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
