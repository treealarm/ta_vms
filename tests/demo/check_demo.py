#!/usr/bin/env python3
"""End-to-end check of the demo world through the gateway.

The world is installed through the same API the administrator's button uses, then walked the way
an operator walks it: map -> building (the floor is the room) -> rack -> switch -> port, with the
types and their regions, the camera placements, the video wall, the lamp the camera's relay lights
and the port that no longer alarms. It then proves what the registry
is for — a second install changes no id, the administrator's own node survives both an install and
a removal, and a removal takes only the demo.

    tests/demo/check_demo.py                    # install if needed, then check
    tests/demo/check_demo.py --provision-cameras  # also add the OnvifSim cameras first
    tests/demo/check_demo.py --keep-installed     # leave the world installed (the default)
"""

import argparse
import json
import sys
import time
import urllib.parse
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "onvifsim"))

import world as W  # noqa: E402
from labhttp import HttpError, request  # noqa: E402
from simapi import Sim  # noqa: E402
from vmsapi import Vms  # noqa: E402

DEMO_KEY = "umd-datacenter"
MAP_NAME = "UMD Data Center (demo)"
BUILDING_NAME = "First floor"
# The diagram standing on that plan: the same room as a schematic, with the racks drawn in it.
LAYOUT_NAME = "First floor Diagram"
# The world names its cameras; VmsCfg keys them by UUID, the same deterministic id the seeder
# derives (DemoIds.Camera — a UUIDv5 of the demo namespace and "camera/<name>").
DEMO_CAMERA_NAMESPACE = uuid.UUID("0f3c1a52-6f5b-4a0e-9a19-3a8a5a4b7c11")
DEMO_CAMERAS = ["sim-master", "sim-ptz", "sim-master2", "sim-ptz2", "sim-vehicle"]
# The reaction the demo installs: a fixed id, so a reinstall updates the same rule and the script
# can name it (DemoRouting.SubscriptionId).
DEMO_SUBSCRIPTION = "053df083-59b0-56b3-a28c-c5701d1fc3cf"
# The sensor the demo configures: sim-ptz's input, wired to sim-master's `peer` relay.
SENSOR_CAMERA = "sim-ptz"
SENSOR_TOKEN = "input"
LAMP_RELAY_CAMERA = "sim-master"
LAMP_RELAY_TOKEN = "lamp"
# The operator's own sensor: sim-master's input, wired to its `loop` relay in the same simulator
# world. It is installed following its camera, so the operator's switch is what arms it.
ARMED_CAMERA = "sim-master"
ARMED_TOKEN = "input"
ARMED_RELAY_TOKEN = "loop"


def camera_id(name):
    return str(uuid.uuid5(DEMO_CAMERA_NAMESPACE, f"camera/{name}"))

failures = []


def check(condition, message):
    if condition:
        print(f"  ok    {message}")
    else:
        print(f"  FAIL  {message}")
        failures.append(message)
    return condition


def warn(message):
    print(f"  ..    {message}")


# ── The API, in the shapes the browser uses ───────────────────────────────────────


def children(vms, parent_id):
    query = f"?parentId={parent_id}&count=1000"
    return (vms.call("GET", f"/api/map/objects/parent{query}") or {}).get("children") or []


def by_name(vms, nodes, name):
    return next((node for node in nodes if node.get("name") == name), None)


def spaces(vms):
    return vms.call("GET", "/api/spatial/spaces") or []


def demo_status(vms):
    return vms.call("GET", "/api/map/admin/demo")


def install_demo(vms):
    return vms.call("POST", "/api/map/admin/demo/install", timeout=180.0)


def remove_demo(vms):
    return vms.call("POST", "/api/map/admin/demo/remove", timeout=180.0)


def diagrams_by_ids(vms, ids):
    return (vms.call("POST", "/api/map/diagrams/by-ids", ids) or {}).get("diagrams") or []


def diagram_types(vms):
    return (vms.call("POST", "/api/map/diagrams/types", {}) or {}).get("diagramTypes") or []


def alarmed(vms):
    return (vms.call("POST", "/api/map/states/alarmed", []) or {}).get("states") or []


def states_of(vms, ids):
    rows = (vms.call("POST", "/api/map/states", ids) or {}).get("states") or []
    return {row.get("id"): row.get("states") or [] for row in rows}


def set_relay(vms, camera_id, token, active):
    vms.call("POST", f"/api/DeviceIo/cameras/{camera_id}/relay-outputs/{token}/state",
             {"active": active})


def relay_state(vms, name, token):
    """One relay's row as the card sees it: the live value and whether it is still a wish."""
    rows = vms.call("GET", f"/api/DeviceIo/cameras/{camera_id(name)}/relay-outputs") or []
    return next((candidate for candidate in rows if candidate.get("token") == token), None)


def relay_live(vms, name, token):
    """A relay's live state, as the camera reports it: None until it has said anything."""
    row = relay_state(vms, name, token)
    return None if row is None else row.get("liveActive")


def input_live(vms, name, token):
    """A digital input's live state, as the camera reports it: None until it has said anything."""
    rows = vms.call("GET", f"/api/DeviceIo/cameras/{camera_id(name)}/digital-inputs") or []
    row = next((candidate for candidate in rows if candidate.get("token") == token), None)
    return None if row is None else row.get("liveActive")


def digital_input_row(vms, name, token):
    """One input as the operator's card sees it: its mode, its effective arming and the right."""
    rows = vms.call("GET", f"/api/DeviceIo/cameras/{camera_id(name)}/digital-inputs") or []
    return next((candidate for candidate in rows if candidate.get("token") == token), None)


def set_arm_mode(vms, name, token, mode):
    """The operator's guard switch, as the browser sends it: the mode and nothing else."""
    vms.call("PUT",
             f"/api/DeviceIo/cameras/{camera_id(name)}/digital-inputs/{token}/arm-mode",
             {"armMode": mode})


# ── The logical tree, the journal, alarms and the device settings ────────────────


def map_search(vms, text):
    """The logical tree searched on the server: a name, a part token or a camera id."""
    query = urllib.parse.urlencode({"query": text, "limit": 50})
    return (vms.call("GET", f"/api/map/objects/search?{query}") or {}).get("markers") or []


def marker(vms, object_id):
    rows = (vms.call("POST", "/api/map/objects/markers", [object_id]) or {}).get("markers") or []
    return rows[0] if rows else None


def ancestor_chain(vms, start):
    """The nodes above one, root last — the path an alarm's state bubbles up."""
    chain = []
    current = start
    while current and current.get("parentId"):
        current = marker(vms, current["parentId"])
        if current:
            chain.append(current)
    return chain


def device_nodes(vms, name):
    """The camera's canonical node and its parts, as the demo's adapter wrote them.

    The search joins every node to its device_node row, so a camera id answers with the canonical
    node under its server and that node's parts. A picture of the camera is a replica with no
    device_node row of its own and is not returned; the canonical node is the one that is nobody's
    replica.
    """
    camera_uuid = camera_id(name)
    found = map_search(vms, camera_uuid)
    camera_node = next(
        (row for row in found if row.get("cameraId") == camera_uuid and not row.get("ownerId")),
        None) or next((row for row in found if row.get("cameraId") == camera_uuid), None)
    parts = [row for row in found if camera_node and row.get("parentId") == camera_node["id"]]
    return camera_node, parts


def figures_of(vms, ids):
    return (vms.call("POST", "/api/map/objects/by-ids", ids) or {}).get("figs") or []


def device_settings(vms, camera_uuid):
    return vms.call("GET", f"/api/DeviceIo/cameras/{camera_uuid}/settings") or []


def set_device_settings(vms, camera_uuid, settings):
    vms.call("PUT", f"/api/DeviceIo/cameras/{camera_uuid}/settings", settings)


def device_io_ready(vms, name):
    """Whether the camera's inputs and relays report a live state.

    A camera that was just added, or a stack that just started, needs a moment before VmsOnvif's
    watcher has heard from the device; a check that starts before that reads nothing and would
    report the demo broken rather than slow."""
    rows = vms.call("GET", f"/api/DeviceIo/cameras/{camera_id(name)}/digital-inputs") or []
    rows += vms.call("GET", f"/api/DeviceIo/cameras/{camera_id(name)}/relay-outputs") or []
    # An empty list is not readiness: a camera just added has no parts until the device declares
    # them, and `all([])` would call that ready and let the next check read nothing.
    return bool(rows) and all(row.get("liveActive") is not None for row in rows)


def events_of(vms, object_id, event_name=None):
    body = {"objects": [object_id], "count": 200}
    events = (vms.call("POST", "/api/map/events/filter", body) or {}).get("events") or []
    return [event for event in events
            if event_name is None or event.get("eventName") == event_name]


def alarms(vms):
    return vms.call("POST", "/api/map/alarms", {"pageSize": 100}) or {}


def open_alarm(vms, object_id):
    for alarm in (alarms(vms).get("open") or {}).get("alarms") or []:
        if alarm.get("objectId") == object_id:
            return alarm
    return None


def active_alarm(vms, object_id):
    """The alarm with a live condition. An earlier occurrence may still be open and returned: the
    journal row and ALARM follow the rule's own transaction, so the test waits for the reactivation
    rather than for any row."""
    for alarm in (alarms(vms).get("open") or {}).get("alarms") or []:
        if alarm.get("objectId") == object_id and alarm.get("status") == "active":
            return alarm
    return None


def close_reason_of(vms, alarm_id):
    """The alarm's current row wherever it is: open, or in the recently-closed branch."""
    for branch in ("open", "closed"):
        for alarm in (alarms(vms).get(branch) or {}).get("alarms") or []:
            if alarm.get("id") == alarm_id:
                return alarm
    return None


def acknowledge(vms, alarm_id):
    """The HTTP status: 200 when the transition happened, 409 when the condition is still active."""
    try:
        vms.call("POST", f"/api/map/alarms/{alarm_id}/acknowledge", {})
        return 200
    except HttpError as e:
        return e.status


def retention_groups(vms, since):
    query = urllib.parse.urlencode({"from": since, "limit": 100})
    return (vms.call("GET", f"/api/fs/retention-events?{query}") or {}).get("groups") or []


def entity_states(vms):
    return vms.call("GET", "/api/entitystate") or []


def subscriptions(vms):
    return (vms.call("GET", "/api/map/admin/event-subscriptions") or {}).get("subscriptions") or []


def wait_until(probe, timeout=30.0, interval=1.0):
    """Polls until the probe answers something truthy, then answers its last value."""
    deadline = time.monotonic() + timeout
    answer = probe()
    while not answer and time.monotonic() < deadline:
        time.sleep(interval)
        answer = probe()
    return answer


def is_alarmed(vms, object_id):
    return "ALARM" in (states_of(vms, [object_id]).get(object_id) or [])


def alarm_reaches(vms, object_id):
    """Whether the node paints as alarmed: its own alarm, or an alarmed child below it. Bubbling is
    the `children_alarms` counter, not an `ALARM` string copied onto every ancestor."""
    rows = (vms.call("POST", "/api/map/states/alarmed", []) or {}).get("states") or []
    row = next((entry for entry in rows if entry.get("id") == object_id), None)
    return bool(row and (row.get("alarm") is True or (row.get("childrenAlarms") or 0) > 0))


# ── The simulator's own world ─────────────────────────────────────────────────────


def sim_world(sim):
    return request("GET", f"{sim.base}/api/world")


def lamp_lit(world, lamp_id):
    """A lamp's state as OnvifSim derives it: a wire from a closed relay, NO/NC taken into account.

    There is no endpoint for it — the lamp is not a device with a state of its own — so the same
    rule the simulator's renderer uses (`DeviceIo.LampLit`) is applied to the world it serves.
    """
    lamp = next((lamp for lamp in world.get("lamps") or [] if lamp.get("id") == lamp_id), None)
    if lamp is None:
        return None

    wires = [wire for wire in world.get("wires") or [] if wire.get("lampId") == lamp_id]
    if not wires:
        return False  # unconnected: never lit

    closed = False
    for wire in wires:
        camera = next((c for c in world.get("cameras") or []
                       if c.get("id") == wire.get("relayCameraId")), None)
        relay = next((r for r in (camera or {}).get("relayOutputs") or []
                      if r.get("token") == wire.get("relayToken")), None)
        if relay and relay.get("active"):
            closed = True

    return (not closed) if lamp.get("normallyClosed") else closed


def levels(vms):
    return (vms.call("GET", "/api/map/levels") or {}).get("levels") or []


def placements(vms):
    return vms.call("GET", "/api/spatial/placements") or []


def monitors(vms):
    return vms.call("GET", "/api/monitor") or []


# ── Provisioning ──────────────────────────────────────────────────────────────────


def provision_cameras(vms, sim_host, server_id):
    """The five OnvifSim cameras, added the way the bench's provision step adds them.

    The demo installs them itself now (VmsCfg, from DEMO_ONVIFSIM_HOST); this remains for a
    deployment installed before that, and finds the cameras already there otherwise."""
    existing = {camera.get("cameraId") for camera in vms.cameras()}
    # `geo_from_device` only for the camera on the vehicle: its place on the map is the camera's
    # own ONVIF position, which the map follows. The others are placed by hand.
    wanted = [("sim-master", 8101, False, False), ("sim-ptz", 8102, True, False),
              ("sim-master2", 8103, False, False), ("sim-ptz2", 8104, True, False),
              ("sim-vehicle", 8105, False, True)]

    for name, port, telemetry, geo_from_device in wanted:
        cam_id = camera_id(name)
        if cam_id in existing:
            warn(f"camera {name} is already configured")
            continue

        vms.add_camera({
            "cameraId": cam_id,
            "name": f"OnvifSim {name}",
            "serverId": server_id,
            "onvifConn": {"ip": sim_host, "port": port,
                          "xaddr": f"http://{sim_host}:{port}/onvif/device_service"},
            "cred": {"user": "admin", "pass": "admin"},
            "hasTelemetry": telemetry,
            "geoFromDevice": geo_from_device,
            "streams": [{"streamName": "main", "profileToken": "Profile_2",
                         "path": "", "webRtc": True}],
        })
        print(f"  ..    added camera {name} (ONVIF {sim_host}:{port})")


def subscribe_events(vms):
    """Turns on the optional ONVIF event subscription for the demo cameras.

    I/O state does not depend on it — VmsOnvif opens its own Device/Trigger subscription
    automatically when a camera has devices — but the demo's alarm filters and the ONVIF settings
    page do, and a demo installed with events off makes that look broken.
    """
    for name in DEMO_CAMERAS:
        try:
            vms.call("POST", f"/api/onvif/cameras/{camera_id(name)}/subscription")
        except HttpError as e:
            warn(f"could not subscribe {name} to ONVIF events: {e}")


# ── Checks ────────────────────────────────────────────────────────────────────────


def walk_the_world(vms):
    """Returns the nodes of the documented path, deepest last, or None when the world is absent."""
    demo_map = by_name(vms, [s for s in spaces(vms) if not s.get("parentId")], MAP_NAME)
    if not check(demo_map is not None, f"the map '{MAP_NAME}' exists"):
        return None

    building = by_name(vms, children(vms, demo_map["id"]), BUILDING_NAME)
    if not check(building is not None and building.get("nodeKind") == "space",
                 f"the map holds '{BUILDING_NAME}' as a plan"):
        return None

    floor = children(vms, building["id"])

    # The floor is the room: no plan is nested in it, so the hall pair and the person's walk are on
    # this one plan. What stands in the room is one diagram of the floor.
    check(not any(node.get("nodeKind") == "space" for node in floor),
          "the floor holds no plan of a room of its own")

    layout = by_name(vms, floor, LAYOUT_NAME)
    if not check(layout is not None and layout.get("nodeKind") == "diagram",
                 f"the floor holds the '{LAYOUT_NAME}' diagram"):
        return None

    # The four racks are regions of the floor's schematic, and every rack is filled with switches.
    # An administrator may draw something else on the same diagram, so the world's racks are what
    # is checked for, not the absence of anything else.
    layout_racks = children(vms, layout["id"])
    expected_racks = {f"Rack A-{index:02}" for index in range(1, 5)}
    drawn = {r.get("name") for r in layout_racks}
    check(expected_racks <= drawn,
          f"the floor diagram shows Rack A-01 … A-04 (got {sorted(drawn)})")

    rack = by_name(vms, layout_racks, "Rack A-01")
    if not check(rack is not None and rack.get("nodeKind") == "diagram",
                 "the floor diagram holds Rack A-01 as a mnemoscheme"):
        return None

    switches = children(vms, rack["id"])
    check(len(switches) == 6, f"Rack A-01 takes a switch in each of its six slots (got {len(switches)})")

    computer = by_name(vms, switches, "Switch A-01 U1")
    if not check(computer is not None and computer.get("nodeKind") == "diagram",
                 "Rack A-01 holds Switch A-01 U1"):
        return None

    ports = children(vms, computer["id"])
    check(len(ports) == 8, f"Switch A-01 U1 holds 8 ports (got {len(ports)})")
    port = by_name(vms, ports, "Port 3")
    check(port is not None, "Port 3 is among them")

    return {
        "map": demo_map, "building": building, "layout": layout,
        "layout_racks": layout_racks, "rack": rack,
        "switch": computer, "port": port, "ports": ports,
    }


def check_plans(vms, world):
    """The plans are drawings of the world, not pictures of something else: vector, and the
    building's footprint is the simulator's own outline corner for corner, so the plan on the map
    lies in the building the cameras stand in.

    A node listing carries neither a plan nor a footprint — those are the space's, from
    `api/spatial/spaces`, which the map itself is served by."""
    spaces_by_id = {space.get("id"): space for space in spaces(vms)}
    space = spaces_by_id.get(world["building"].get("id")) or {}
    mime = ((space.get("plan") or {}).get("mime") or "")
    check(mime == "image/svg+xml",
          f"the plan of {BUILDING_NAME} is a drawing, not a picture ({mime or 'none'})")
    check(not any(child.get("parentId") == world["building"].get("id") for child in spaces(vms)),
          "no plan of a room is nested in the floor")

    floor = spaces_by_id.get(world["building"].get("id")) or {}
    footprint = [(point.get("lat"), point.get("lon")) for point in floor.get("footprint") or []]
    check(len(footprint) == len(W.MARYLAND_BUILDING_OUTLINE),
          f"the floor's footprint has {len(W.MARYLAND_BUILDING_OUTLINE)} corners (got {len(footprint)})")
    for x, z in W.MARYLAND_BUILDING_OUTLINE:
        lat, lon = W.to_wgs84(x, z, W.MARYLAND_LAT0, W.MARYLAND_LON0)
        check(any(W.ground_gap(lat, lon, flat, flon) <= 0.01 for flat, flon in footprint),
              f"the footprint has the world's corner ({lat:.7f}, {lon:.7f})")


def check_diagram_marker(vms, world):
    """The floor diagram is marked where the simulator's racks stand: the middle of the row along
    Room 101's west wall. A marker in the building's east wing looks like a diagram of some other
    room."""
    layout = world["layout"]
    figs = figures_of(vms, [layout["id"]])
    fig = next((candidate for candidate in figs if candidate.get("id") == layout["id"]), None)
    coord = ((fig or {}).get("geometry") or {}).get("coord") or []
    if not check(coord, "the floor diagram has a point on the plan"):
        return

    lat, lon = coord[0].get("lat"), coord[0].get("lon")
    x = W.MARYLAND_RACK_X
    z = sum(W.MARYLAND_RACK_Z) / len(W.MARYLAND_RACK_Z)
    expected = W.to_wgs84(x, z, W.MARYLAND_LAT0, W.MARYLAND_LON0)
    gap = W.ground_gap(lat, lon, *expected[:2])
    check(gap <= 0.5, f"the diagram stands {gap:.2f} m from the rack row "
                      f"({expected[0]:.7f}, {expected[1]:.7f})")


def check_positions(vms, world):
    """Regions give the place; the geometry stays the diagram's own canvas (M2.2 step 1)."""
    ids = [world[key]["id"] for key in ("rack", "switch", "port", "layout")]
    ids += [rack["id"] for rack in world["layout_racks"]]
    diagrams = {d["id"]: d for d in diagrams_by_ids(vms, ids)}

    layout = diagrams.get(world["layout"]["id"], {})
    check(layout.get("geometry", {}).get("width") == 800 and layout.get("geometry", {}).get("height") == 600,
          "the floor diagram opens on its own 800x600 canvas")
    check(not layout.get("regionId"), "the floor diagram stands by its own rectangle on the floor")

    # Every rack fills a region of the floor's schematic, and keeps a canvas of its own inside it.
    # The children come back unordered; the rack's own name says which region it fills. Anything
    # else on the diagram is an administrator's, not the world's, and is left alone.
    for rack in [r for r in world["layout_racks"] if (r.get("name") or "").startswith("Rack A-")]:
        index = int(rack["name"].rsplit("-", 1)[1])
        placed = diagrams.get(rack["id"], {})
        check(placed.get("regionId") == f"rack-{index}",
              f"{rack['name']} fills the floor diagram's rack-{index} region")
        check(placed.get("geometry", {}).get("width") == 240,
              f"{rack['name']} keeps its own 240-wide canvas beside the region")

    computer = diagrams.get(world["switch"]["id"], {})
    check(computer.get("regionId") == "slot-1", "the switch sits in the rack's slot-1 region")
    check(computer.get("geometry", {}).get("width") == 600,
          "the switch keeps its own 600-wide canvas beside the region")

    port = diagrams.get(world["port"]["id"], {})
    check(port.get("regionId") == "port-3", "Port 3 sits in the switch's port-3 region")


def check_device_diagram(vms, world):
    """The camera drawn from its model: a node under the camera's picture on the plan, not beside
    it at the plan's root and not under the device in the logical tree — and not drawn there, since
    the map offers a link into it beside the selected camera instead of a card that always stands
    on the plan. Its sockets hold the parts that were configured — and a socket lights up with its
    relay, because a replica shows its owner's state rather than keeping one of its own."""
    camera_node, parts = device_nodes(vms, "sim-master")
    if not check(camera_node is not None, "sim-master has a canonical node"):
        return

    picture = next(
        (kid for kid in children(vms, world["building"]["id"])
         if kid.get("cameraId") == camera_node.get("cameraId")
         and kid.get("ownerId") == camera_node.get("id")),
        None)
    if not check(picture is not None, "the camera's picture stands on the plan"):
        return

    camera_name = camera_node.get("name") or ""
    # The demo ships it with the world; a diagram an administrator places appears through the
    # node's own action, so this waits only for the world's own.
    diagram = wait_until(
        lambda: next((kid for kid in children(vms, picture["id"])
                      if kid.get("nodeKind") == "diagram"
                      and (kid.get("name") or "").startswith(f"{camera_name} /")), None),
        60)
    if not check(diagram is not None, "the camera's rear panel hangs under its picture"):
        return

    check((diagram.get("name") or "").endswith("Camera rear panel"),
          f"the diagram is named after its type (has {diagram.get('name')!r})")

    # Not drawn: the floor carries one point per camera, and the diagram is reached by the link
    # the map offers beside the selected camera. A picture beside it is an administrator's choice.
    figure = next((candidate for candidate in figures_of(vms, [diagram["id"]])
                   if candidate.get("id") == diagram["id"]), None)
    check(figure is None, "the diagram is not drawn on the plan")

    content = vms.call("GET", f"/api/map/diagrams/{diagram['id']}/content?depth=1") or {}
    rows = {row.get("id"): row for row in content.get("content") or []}
    replicas = [row for row in content.get("children") or [] if row.get("id") in rows]

    regions = {rows[row["id"]].get("regionId") for row in replicas}
    check(regions == {"input", "lamp", "peer", "loop"},
          f"the panel's four sockets hold a replica each (has {sorted(regions)})")

    peer = next((part for part in parts if part.get("name") == "Peer"), None)
    replica = next((row for row in replicas if row.get("ownerId") == (peer or {}).get("id")), None)
    if not check(replica is not None, "the peer socket shows the peer relay"):
        return

    check(states_of(vms, [replica["id"]]).get(replica["id"]) is not None,
          "the replica's states are readable through its owner")

    set_relay(vms, camera_id("sim-master"), "peer", True)
    lit = wait_until(
        lambda: "RELAY_ACTIVE" in (states_of(vms, [replica["id"]]).get(replica["id"]) or []), 40)
    set_relay(vms, camera_id("sim-master"), "peer", False)
    check(lit, "closing `peer` lights its socket on the panel")

    dark = wait_until(
        lambda: "RELAY_INACTIVE" in (states_of(vms, [replica["id"]]).get(replica["id"]) or []), 40)
    check(dark, "opening it again puts the socket out")


def check_types(vms):
    types = {t.get("name"): t for t in diagram_types(vms)}

    rack = types.get("Rack 42U", {})
    rack_regions = {r.get("regionKey") for r in rack.get("regions") or []}
    check({"slot-1"} <= rack_regions, f"Rack 42U offers its slots (has {sorted(rack_regions)})")
    check(not ({"temp", "humidity"} & rack_regions),
          "Rack 42U no longer shows the synthetic temperature and humidity")

    check("Device telemetry" not in types,
          "the readings panel the world used to ship is gone")

    layout = types.get("Floor layout", {})
    layout_regions = {r.get("regionKey") for r in layout.get("regions") or []}
    check(layout_regions == {"rack-1", "rack-2", "rack-3", "rack-4"},
          f"Floor layout offers its four rack regions (has {sorted(layout_regions)})")

    computer = types.get("Cisco Catalyst 9300", {})
    computer_regions = {r.get("regionKey") for r in computer.get("regions") or []}
    check(len(computer_regions) == 8 and "port-8" in computer_regions,
          "Cisco Catalyst 9300 offers its eight ports")

    check("RJ45 Port" in types, "the port type is in the dictionary")

    panel = types.get("Camera rear panel", {})
    panel_regions = {r.get("regionKey") for r in panel.get("regions") or []}
    check({"input", "lamp", "peer", "loop"} == panel_regions,
          f"Camera rear panel offers its four sockets (has {sorted(panel_regions)})")
    check(panel.get("sourceKind") == "camera" and panel.get("autoAttach") is True,
          "the panel is a template that attaches itself to its model")
    bindings = {r.get("regionKey"): (r.get("part") or {}) for r in panel.get("regions") or []}
    check(bindings.get("peer", {}).get("matchValue") == "peer",
          "the peer socket finds its part by token")


def check_cameras(vms, world, allow_missing):
    configured = {camera.get("cameraId") for camera in vms.cameras()}
    missing = [name for name in DEMO_CAMERAS if camera_id(name) not in configured]

    if missing and not allow_missing:
        check(False, f"the four demo cameras are configured (missing {', '.join(missing)})")
        return

    if missing:
        warn(f"missing cameras skipped: {', '.join(missing)}")
        return

    placed = {p.get("cameraId"): p for p in placements(vms)}
    # Each pair works as one: the hall pair stands on the floor plan — the floor is the room, and
    # the master looks along the line out of the hall to the doorway; the yard pair out on the map
    # beyond the building, and the camera on the car there too.
    expected = {
        camera_id("sim-master"): world["building"]["id"],
        camera_id("sim-ptz"): world["building"]["id"],
        camera_id("sim-master2"): world["map"]["id"],
        camera_id("sim-ptz2"): world["map"]["id"],
        # Only its starting point: the camera is flagged geo_from_device and follows its own ONVIF
        # position from then on, so only the space it belongs to is fixed here.
        camera_id("sim-vehicle"): world["map"]["id"],
    }

    for cam_id, space_id in expected.items():
        placement = placed.get(cam_id)
        check(placement is not None and placement.get("spaceId") == space_id,
              f"{cam_id} stands in its space")


def check_calibration(vms, world, allow_missing):
    """The demo arrives calibrated: every landmark is the simulator's own block, and each camera's
    marks are the projection of those blocks from its pose. The expected numbers are world.py's,
    computed from the world and checked against it (``world.py --check-maryland``), so a drift here
    means the map, the world and the pinned constants have parted ways."""
    pairs = (
        ("hall", W.MARYLAND_HALL_LANDMARKS, world["building"]["id"],
         "sim-master", "sim-ptz", W.MARYLAND_HALL_GROUND, W.MARYLAND_HALL_PTZ, True),
        ("road", W.MARYLAND_ROAD_LANDMARKS, world["map"]["id"],
         "sim-master2", "sim-ptz2", W.MARYLAND_ROAD_GROUND, W.MARYLAND_ROAD_PTZ, False),
    )
    configured = {camera.get("cameraId") for camera in vms.cameras()}

    for name, landmarks, space_id, master_name, slave_name, ground_pins, ptz_pins, with_zoom in pairs:
        master, slave = camera_id(master_name), camera_id(slave_name)
        if master not in configured or slave not in configured:
            if allow_missing:
                warn(f"the {name} pair is not configured: its calibration is not checked")
            else:
                check(False, f"the {name} pair is configured, so its calibration can be checked")
            continue

        placed = {landmark["name"].split()[0]: landmark
                  for landmark in vms.landmarks(space_id) if landmark.get("name")}
        for landmark in landmarks:
            found = placed.get(landmark.name)
            if not check(found is not None, f"the {name} space holds the {landmark.label} landmark"):
                continue
            lat, lon = W.to_wgs84(landmark.x, landmark.z, W.MARYLAND_LAT0, W.MARYLAND_LON0)
            gap = W.ground_gap(found["lat"], found["lon"], lat, lon)
            check(gap <= 0.5, f"{landmark.label} stands {gap:.2f} m from the world's block")
            check(abs(found.get("heightM") or 0) <= 0.01, f"{landmark.label} stands on the ground")

        short_name = {landmark["id"]: name.split()[0] for name, landmark in placed.items()}

        ground = vms.get_ground_geo(master) or {}
        marks = {short_name.get(mark.get("landmarkId")): mark
                 for mark in ground.get("observations") or []}
        check(len(marks) == len(landmarks),
              f"{master_name} marks all {len(landmarks)} landmarks (has {len(marks)})")
        check(ground.get("fitted") is True,
              f"{master_name}'s ground fit is made ({ground.get('error')})")
        for landmark in landmarks:
            mark = marks.get(landmark.name)
            if not check(mark is not None, f"{master_name} marks {landmark.label}"):
                continue
            ex, ey = ground_pins[landmark.name]
            check(abs(mark["x"] - ex) <= 1e-3 and abs(mark["y"] - ey) <= 1e-3,
                  f"{master_name}'s mark at {landmark.label} is "
                  f"({mark['x']:.6f}, {mark['y']:.6f}); the world projects ({ex}, {ey})")

        ptz = vms.get_ptz_geo(slave) or {}
        observations = {short_name.get(row.get("landmarkId")): row
                        for row in ptz.get("observations") or []}
        check(len(observations) == len(landmarks),
              f"{slave_name} observes all {len(landmarks)} landmarks (has {len(observations)})")
        check(ptz.get("fitted") is True, f"{slave_name}'s PTZ fit is made ({ptz.get('error')})")
        for landmark in landmarks:
            row = observations.get(landmark.name)
            if not check(row is not None, f"{slave_name} observes {landmark.label}"):
                continue
            ex, ey, *rest = ptz_pins[landmark.name]
            check(abs(row["pan"] - ex) <= 1e-3 and abs(row["tilt"] - ey) <= 1e-3,
                  f"{slave_name}'s observation of {landmark.label} is "
                  f"({row['pan']:.6f}, {row['tilt']:.6f}); the world projects ({ex}, {ey})")
            if with_zoom:
                check(row.get("zoom") is not None and abs(row["zoom"] - rest[0]) <= 1e-3,
                      f"{slave_name}'s zoom at {landmark.label} is the framing zoom ({rest[0]})")
            else:
                check(row.get("zoom") is None, f"{slave_name} keeps the wide lens")

        binding = vms.get_binding(slave) or {}
        check(binding.get("masterCameraId") == master,
              f"{slave_name} is bound to {master_name} (got {binding.get('masterCameraId')})")
        check(binding.get("method") == "geo", f"{slave_name} is aimed through the map")
        check(not binding.get("unusable"), f"{slave_name}'s binding can aim ({binding.get('unusable')})")

        policy = vms.call("GET", f"/api/ptz-tracking-policy/{slave}") or {}
        check(policy.get("enabled") is True, f"{slave_name} follows its master")
        check("person" in (policy.get("kinds") or []), f"{slave_name} follows people")
        check("onvif" in (policy.get("sources") or []), f"{slave_name} follows the master's own boxes")


def check_camera_poses(vms, allow_missing):
    """The four placed cameras stand exactly where the demo world puts them.

    The world file and DemoWorlds.cs carry the same constants, so a drift here means one of the
    three (this script, the map, the simulator) was changed without the others."""
    placed = {p.get("cameraId"): p for p in placements(vms)}
    for placement in W.DEMO_PLACEMENTS:
        cam_id = camera_id(f"sim-{placement.id}")
        if cam_id not in placed and allow_missing:
            warn(f"{cam_id} is not placed: its pose is not checked")
            continue
        spot = placed.get(cam_id)
        if not check(spot is not None, f"{cam_id} is placed on the map"):
            continue
        gap = W.ground_gap(spot.get("lat", 0.0), spot.get("lon", 0.0), placement.lat, placement.lon)
        check(gap <= W.MARYLAND_POSITION_TOLERANCE_M,
              f"{cam_id} stands where the world puts it ({gap:.2f} m off)")
        check(abs((spot.get("heightM") or 0.0) - placement.height) <= W.MARYLAND_POSITION_TOLERANCE_M,
              f"{cam_id} is {placement.height} m up")
        check(W.angle_gap(spot.get("headingDeg") or 0.0, placement.heading) <= W.MARYLAND_ANGLE_TOLERANCE_DEG,
              f"{cam_id} faces {placement.heading}°")
        check(abs((spot.get("hfovDeg") or 0.0) - placement.hfov) <= W.MARYLAND_ANGLE_TOLERANCE_DEG,
              f"{cam_id} is {placement.hfov}° wide")


def check_geo_flag(vms):
    cameras = {camera.get("cameraId"): camera for camera in vms.cameras()}
    check(cameras.get(camera_id("sim-vehicle"), {}).get("geoFromDevice") is True,
          "sim-vehicle takes its position from the device")
    for name in ("sim-master", "sim-master2", "sim-ptz", "sim-ptz2"):
        check(cameras.get(camera_id(name), {}).get("geoFromDevice") is not True,
              f"{name} is placed by hand")


def check_marker_follows_the_camera(vms):
    """The marker moves with the device; a hand-placed camera next to it does not move at all."""
    before = {placement.get("cameraId"): placement for placement in placements(vms)}
    time.sleep(7)  # the follower ticks every 5 s, so at least one move must have been written
    after = {placement.get("cameraId"): placement for placement in placements(vms)}

    vehicle = camera_id("sim-vehicle")
    vehicle_before, vehicle_after = before.get(vehicle), after.get(vehicle)
    if check(vehicle_before is not None and vehicle_after is not None,
             "sim-vehicle is placed on the map"):
        moved = ((vehicle_before.get("lat"), vehicle_before.get("lon"))
                 != (vehicle_after.get("lat"), vehicle_after.get("lon")))
        check(moved, "the vehicle camera's marker moves with the device")

    master = camera_id("sim-master")
    master_before, master_after = before.get(master), after.get(master)
    if master_before and master_after:
        check((master_before.get("lat"), master_before.get("lon"))
              == (master_after.get("lat"), master_after.get("lon")),
              "a hand-placed camera stays where the administrator put it")


def check_events(vms):
    subscribed = {
        camera.get("cameraId")
        for camera in vms.call("GET", "/api/onvif/subscriptions") or []
        if camera.get("subscribed")
    }
    missing = [name for name in DEMO_CAMERAS if camera_id(name) not in subscribed]
    check(not missing, f"the demo cameras are subscribed to ONVIF events (missing {missing})")


def check_monitor(vms, allow_missing):
    wall = next((m for m in monitors(vms) if m.get("name") == "UMD DC"), None)
    if not check(wall is not None, "the 'UMD DC' video wall is in VmsCfg"):
        return

    check(wall.get("layout") == "3x3", "the wall is a 3x3 grid: a row per pair, the car camera last")
    cameras = wall.get("cameraIds") or []
    # The cells, not just the pool: a pair is on a row of its own only if the cells are named.
    cells = {slot.get("slotIndex"): slot.get("cameraId") for slot in wall.get("slots") or []}
    for slot, name in ((0, "sim-master"), (1, "sim-ptz"), (3, "sim-master2"),
                       (4, "sim-ptz2"), (6, "sim-vehicle")):
        cam_id = camera_id(name)
        if allow_missing and cam_id not in cameras:
            continue
        check(cells.get(slot) == cam_id, f"the wall's cell {slot + 1} shows {name}")
    expected = [camera_id(name) for name in DEMO_CAMERAS]
    if allow_missing:
        # A world installed before the simulator's cameras are provisioned: what exists is shown,
        # in the wall's own order.
        present = [cam_id for cam_id in expected if cam_id in cameras]
        check(cameras == present, f"the wall shows the provisioned cameras in its order (got {cameras})")
        warn(f"the wall shows {len(cameras)} camera(s)")
    else:
        missing = [name for name in DEMO_CAMERAS if camera_id(name) not in cameras]
        check(not missing, f"the wall shows all five demo cameras (missing {', '.join(missing)})")
        check(cameras == expected, f"the wall shows the five cameras in its order (got {cameras})")


def check_levels(vms):
    """The dictionary is the map's own names for scales, not a row for every range of zooms."""
    names = {level.get("zoomLevel") for level in levels(vms)}
    expected = {"World", "Continent", "Country", "Region", "City",
                "District", "Street", "Building", "Room"}
    check(expected <= names,
          f"the nine map scales are in the dictionary (missing {sorted(expected - names)})")
    check(not {"Campus", "Rack"} & names,
          f"the old demo layers are gone (has {sorted({'Campus', 'Rack'} & names)})")
    ranges = sorted(name for name in names if name and name[0].isdigit())
    check(not ranges, f"the rows for single zoom ranges are gone (has {ranges[:3]})")


def check_port_not_alarmed(vms, world):
    """The demo owns no device, so no node it writes is in alarm — the port least of all.

    Cameras may legitimately be DISCONNECTED (a stack without OnvifSim), which is why the check
    is about the port and not about the world being alarm-free.
    """
    port_id = world["port"]["id"]
    states = states_of(vms, [port_id]).get(port_id) or []
    check("CONNECTED" in states, f"Port 3 is CONNECTED (has {states})")
    check(not {"DISCONNECTED", "ALARM"} & set(states),
          f"Port 3 carries no alarm state (has {states})")

    alarmed_ids = {row.get("id") for row in alarmed(vms)}
    check(port_id not in alarmed_ids, "Port 3 is not among the alarmed nodes")


def check_no_camera_replica(vms):
    """The camera on the car keeps no replica of its own.

    A diagram stands where it is put, and `sim-vehicle` drives the ring road: its place is the car
    on the map, which follows what the camera reports (geo_from_device). The diagram on the floor is
    the indoor master's, which does stand still.
    """
    found = map_search(vms, camera_id("sim-vehicle"))
    if not found:
        warn("the camera on the car has no node yet: its replica is not checked")
        return
    check(len(found) == 1, f"nothing copies the camera on the car ({len(found)} nodes)")


def check_wired_devices(vms, sim):
    """`sim-master`'s relays: one drives `sim-ptz`'s sensor, one lights the hall's lamp.

    Switching them changes what the wired sensor reports — read from the camera's own device I/O,
    which is where the map's readings used to come from — and the simulator's own lamp, which is
    not a device with a state, so its rule (any wired relay closed) is applied to its world.
    """
    # A camera that was deleted and re-provisioned by an earlier run needs a moment before its
    # watcher has heard from the device: wait for the readings rather than call the demo broken.
    ready = wait_until(
        lambda: relay_live(vms, LAMP_RELAY_CAMERA, "peer") is not None
        and input_live(vms, SENSOR_CAMERA, SENSOR_TOKEN) is not None, 60)
    if not check(bool(ready), "the demo starts with a relay and a sensor the cameras report"):
        return

    def switch(active):
        set_relay(vms, camera_id("sim-master"), "peer", active)
        # The camera's event (its pull point is polled), then VmsOnvif's cache. Polling for the
        # reading rather than sleeping a fixed time keeps a slower source from turning into a false
        # failure. The input's idle state is open, so the closed circuit is its active state.
        wait_until(lambda: relay_live(vms, LAMP_RELAY_CAMERA, "peer") == active, 40)
        wait_until(lambda: input_live(vms, SENSOR_CAMERA, SENSOR_TOKEN) == active, 20)

    switch(True)
    check(relay_live(vms, LAMP_RELAY_CAMERA, "peer") is True,
          "the relay reports itself closed")
    check(input_live(vms, SENSOR_CAMERA, SENSOR_TOKEN) is True,
          "the wired sensor reads 1 (active) while the relay is closed")

    # The lamp is lit by the demo's routing rule, not by this script: the closed contact makes the
    # rule drive the lamp relay. Waiting for it here both proves that delivery and gets it out of
    # the way — a rule action landing after the off phase below would relight the lamp and look
    # like a broken wire. The lamp itself is the simulator's derivation, so poll it.
    check(bool(wait_until(lambda: lamp_lit(sim_world(sim), "alert") is True, 60)),
          "the demo rule lights the lamp in the simulator")

    switch(False)
    set_relay(vms, camera_id("sim-master"), "lamp", False)
    check(relay_live(vms, LAMP_RELAY_CAMERA, "peer") is False,
          "the relay reports itself open again")
    check(input_live(vms, SENSOR_CAMERA, SENSOR_TOKEN) is False,
          "the sensor returns to 0 (idle)")
    check(bool(wait_until(lambda: lamp_lit(sim_world(sim), "alert") is False, 20)),
          "the lamp goes dark again")


def check_vehicle_picture(sim):
    """The camera on the car moves: two pictures taken apart cannot be the same."""
    first = sim.snapshot("vehicle")
    time.sleep(1.0)
    second = sim.snapshot("vehicle")
    check(len(first) > 1000, f"OnvifSim renders the vehicle picture ({len(first)} bytes)")
    check(first != second, "the vehicle camera's picture changes as it moves")


# ── Device I/O, alarms and routing: the M2.4e acceptance scenario ───────────────


def sensor_part(vms):
    """The sensor node under sim-ptz: created by the adapter from the settings the demo wrote."""
    def find():
        _, parts = device_nodes(vms, SENSOR_CAMERA)
        return next((part for part in parts if part.get("name") == "Sensor"), None)

    return wait_until(find, 30)


def sensor_settings(vms):
    return next((row for row in device_settings(vms, camera_id(SENSOR_CAMERA))
                 if row.get("kind") == "digital_input" and row.get("token") == SENSOR_TOKEN), None)


def sensor_events(vms, part_id, contact):
    return [event for event in events_of(vms, part_id, "sensor")
            if event.get("param0") == contact]


def check_provisioning(vms):
    """The demo installs the sensor's arming and its rule, not just the map."""
    sensor = wait_until(lambda: sensor_settings(vms), 20)
    if check(sensor is not None, "the demo configured sim-ptz/input"):
        check(sensor.get("armMode") == "armed", f"the input is armed (has {sensor.get('armMode')!r})")
        check(sensor.get("alarmEnabled") is True and sensor.get("triggerOn") == "closed",
              f"the input alarms on the contact the wire produces (has {sensor.get('triggerOn')!r})")

    rule = next((row for row in subscriptions(vms) if row.get("id") == DEMO_SUBSCRIPTION), None)
    check(rule is not None and rule.get("enabled") is True,
          "the demo's sensor-recording rule is installed and enabled")


def check_operator_arming(vms):
    """The operator's guard switch on a camera's own sensor.

    The demo installs `sim-master/input` following its camera and wired to the `loop` relay. This
    arms it from the card, trips it through the relay, and proves the two invariants an operator
    relies on: a repeated arming raises no second alarm, and disarming lets an acknowledgement
    through without taking the alarm away.

    Everything it changes is put back in `finally`: a failure halfway through must not leave the
    demo with a sensor on guard, a relay closed and an alarm active.
    """
    print("Operator arming")

    def row():
        return digital_input_row(vms, ARMED_CAMERA, ARMED_TOKEN)

    # The part's node is the adapter's work, from the device's first snapshot: until it exists the
    # effective arming is unknown, and that is what `armingSource` says.
    listed = wait_until(lambda: (row() or {}).get("armingSource"), 45)
    if not check(bool(listed), "sim-master/input resolves an arming source"):
        return

    node, parts = device_nodes(vms, ARMED_CAMERA)
    part = next((candidate for candidate in parts if candidate.get("name") == "Master sensor"), None)
    if not check(part is not None, "the master sensor is a node under the camera"):
        return
    ancestors = ancestor_chain(vms, part)

    initial = row()
    check(initial.get("armMode") == "inherit",
          f"the sensor starts configured to follow its camera (has {initial.get('armMode')!r})")
    check(initial.get("armed") is False and initial.get("armingSource") == "camera",
          "an inherit sensor on a disarmed camera reads off guard, from the camera")
    check(initial.get("alarmEnabled") is True, "the sensor alarms on its contact")
    check(initial.get("canControl") is True, "the demo's admin may control the camera")

    # A late event from an earlier front can leave the map's copy of the contact closed while the
    # wire is open; this front would then be deduplicated as a repeat. Settle it on the wire first.
    set_relay(vms, camera_id(ARMED_CAMERA), ARMED_RELAY_TOKEN, False)
    settle_sensor(vms, part["id"])

    try:
        set_arm_mode(vms, ARMED_CAMERA, ARMED_TOKEN, "armed")
        armed = wait_until(lambda: (row() or {}).get("armMode") == "armed"
                           and (row() or {}).get("armed") is True
                           and (row() or {}).get("armingSource") == "explicit", 30)
        check(bool(armed), "arming writes 'armed' and the effective state follows at once")

        relay_on = camera_id(ARMED_CAMERA)
        before = {event["id"] for event in sensor_events(vms, part["id"], "closed")}
        set_relay(vms, relay_on, ARMED_RELAY_TOKEN, True)
        wait_until(lambda: input_live(vms, ARMED_CAMERA, ARMED_TOKEN) is True, 40)
        event = wait_until(
            lambda: next((e for e in sensor_events(vms, part["id"], "closed")
                          if e["id"] not in before), None), 45)
        check(event is not None, "closing `loop` writes one `sensor` event with contact=closed")

        alarm = wait_until(lambda: active_alarm(vms, part["id"]), 30)
        if check(alarm is not None, "the armed sensor raises an active alarm"):
            for caused in [part] + ancestors:
                wait_until(lambda target=caused: alarm_reaches(vms, target["id"]), 20)
                check(alarm_reaches(vms, caused["id"]),
                      f"ALARM reaches {caused.get('name') or caused['id']}")

            # The same command again is not a second arming: the alarm is deduplicated by its key,
            # so a lost or repeated notification cannot double it.
            set_arm_mode(vms, ARMED_CAMERA, ARMED_TOKEN, "armed")
            time.sleep(3)
            open_rows = [row for row in (alarms(vms).get("open") or {}).get("alarms") or []
                         if row.get("objectId") == part["id"]]
            check(len(open_rows) == 1,
                  f"arming again keeps exactly one alarm (has {len(open_rows)})")

            status = acknowledge(vms, alarm["id"])
            check(status == 409, f"acknowledging while armed answers 409 (got {status})")

            set_arm_mode(vms, ARMED_CAMERA, ARMED_TOKEN, "disarmed")
            disarmed = wait_until(lambda: (row() or {}).get("armed") is False, 30)
            check(bool(disarmed), "disarming reads off guard")
            status = wait_until(lambda: acknowledge(vms, alarm["id"]) == 200, 25)
            check(bool(status), "acknowledging after disarming succeeds")
            check(is_alarmed(vms, part["id"]),
                  "acknowledging does not take ALARM away: the condition still holds")

            # The contact returning is what closes the alarm, on a disarmed sensor as on an armed one.
            opened = {e["id"] for e in sensor_events(vms, part["id"], "open")}
            set_relay(vms, relay_on, ARMED_RELAY_TOKEN, False)
            wait_until(lambda: input_live(vms, ARMED_CAMERA, ARMED_TOKEN) is False, 40)
            returned = wait_until(
                lambda: next((e for e in sensor_events(vms, part["id"], "open")
                              if e["id"] not in opened), None), 45)
            check(returned is not None, "opening `loop` writes a `sensor` event with contact=open")
            check(bool(wait_until(lambda: not is_alarmed(vms, part["id"]), 40)),
                  "ALARM leaves the sensor when the contact returns")
    finally:
        try:
            set_relay(vms, camera_id(ARMED_CAMERA), ARMED_RELAY_TOKEN, False)
        finally:
            restore = sensor_settings_row(vms, ARMED_CAMERA, ARMED_TOKEN)
            if restore:
                set_device_settings(vms, camera_id(ARMED_CAMERA), {**restore, "armMode": "inherit"})


def control_objects(vms, space_id=None, kind=None, for_camera=None, page_size=None, after=None):
    """One page of the operator's Control list, as the panel asks for it."""
    params = {}
    if space_id:
        params["spaceId"] = space_id
    if kind:
        params["kind"] = kind
    if for_camera:
        params["cameraId"] = for_camera
    if page_size:
        params["pageSize"] = str(page_size)
    if after:
        params["afterName"] = after.get("afterName") or ""
        params["afterId"] = after.get("afterId") or ""

    query = f"?{urllib.parse.urlencode(params)}" if params else ""
    return vms.call("GET", f"/api/control/objects{query}") or {}


def control_object(vms, object_id):
    """One object as the map's inspector asks for it: None when it is not an object it may drive."""
    try:
        return vms.call("GET", f"/api/control/objects/{object_id}")
    except HttpError as e:
        if e.status == 404:
            return None
        raise


def check_control_panel(vms):
    """The operator's Control tab: a space's objects, paged, with what each one resolves to.

    The demo has two configured sensors on the first floor, two cameras standing there and
    sim-master's three relays. The check proves the server filters by space, kind and camera,
    pages by keyset, resolves a node id (or a picture of it) to the object, and reads the arming
    and the live state the same way the camera card does.
    """
    print("Control panel")

    floor = next((space for space in spaces(vms) if space.get("name") == BUILDING_NAME), None)
    if not check(floor is not None, f"the demo has the space {BUILDING_NAME!r}"):
        return
    site = next((space for space in spaces(vms) if space.get("name") == MAP_NAME), None)
    if not check(site is not None, f"the demo has the map {MAP_NAME!r}"):
        return

    sensors = control_objects(vms, floor["id"], kind="digital_input").get("objects") or []
    check(sorted(row.get("name") for row in sensors) == ["Master sensor", "Sensor"],
          f"the floor lists its two configured sensors (has {[r.get('name') for r in sensors]!r})")
    check(all(row.get("kind") == "digital_input" and row.get("sourceKind") == "camera"
              and row.get("partToken") == ARMED_TOKEN for row in sensors),
          "each row is addressed by source and part token, not by a node id alone")
    check(all(row.get("spaceName") == BUILDING_NAME for row in sensors),
          "each row is placed in the room its camera stands in")
    check(all(row.get("state") in ("open", "closed", "", None) for row in sensors),
          "a sensor row carries the contact the camera reports")

    master = next((row for row in sensors if row.get("sourceId") == camera_id(ARMED_CAMERA)), None)
    sensor = next((row for row in sensors if row.get("sourceId") == camera_id(SENSOR_CAMERA)), None)
    if check(master is not None and sensor is not None,
             "both the master's sensor and the ptz's are listed"):
        check(master.get("configured") is True and master.get("canControl") is True,
              "the admin's row is configured and may be driven")
        check(master.get("hasArming") is True, "a sensor row is guardable")
        check(master.get("armed") is False and master.get("armingSource") == "camera",
              "the master's sensor follows its disarmed camera")
        check(sensor.get("armed") is True and sensor.get("armingSource") == "explicit",
              "the ptz sensor is explicitly on guard")

    # A camera is an object of its own, and what its row says is its own flag — not what a zone
    # would make its sensors inherit.
    cameras = control_objects(vms, floor["id"], kind="camera").get("objects") or []
    camera_ids = {row.get("sourceId") for row in cameras}
    check({camera_id(ARMED_CAMERA), camera_id(SENSOR_CAMERA)} <= camera_ids,
          f"the floor lists the cameras that stand in it (has {sorted(camera_ids)!r})")
    ptz_camera = next((row for row in cameras if row.get("sourceId") == camera_id(SENSOR_CAMERA)), None)
    if ptz_camera is not None:
        state = vms.call("GET", f"/api/cameras/{camera_id(SENSOR_CAMERA)}/state") or {}
        check(ptz_camera.get("kind") == "camera" and ptz_camera.get("hasArming") is True,
              "a camera row is armable like the card")
        check(ptz_camera.get("armed") == state.get("armed"),
              "a camera row reads the same flag the card reads")

    relays = wait_until(
        lambda: control_objects(vms, floor["id"], kind="relay").get("objects"), 30) or []
    relay_tokens = {row.get("partToken") for row in relays}
    if check({"lamp", "peer", "loop"} <= relay_tokens,
             f"sim-master's relays are listed (has {sorted(relay_tokens)!r})"):
        check(all(row.get("hasArming") is False for row in relays),
              "a relay row is switched, not armed")
        check(all(row.get("state") in ("active", "inactive", "", None) for row in relays),
              "a relay row carries the state the camera reports")
        lamp = next(row for row in relays if row.get("partToken") == LAMP_RELAY_TOKEN)
        check(control_object(vms, lamp["objectId"]) is not None,
              "the map's inspector resolves a relay node by id")

        # A command is a wish until the camera confirms it: the card answers with the wish at once,
        # the list follows when the map hears of it, and both settle on the camera's own report.
        before = relay_state(vms, LAMP_RELAY_CAMERA, LAMP_RELAY_TOKEN) or {}
        target = not bool(before.get("liveActive"))
        expected = "active" if target else "inactive"

        def state_now():
            return (control_object(vms, lamp["objectId"]) or {}).get("state")

        try:
            set_relay(vms, camera_id(LAMP_RELAY_CAMERA), LAMP_RELAY_TOKEN, target)
            check(bool(wait_until(
                lambda: (relay_state(vms, LAMP_RELAY_CAMERA, LAMP_RELAY_TOKEN) or {}).get("liveActive")
                == target, 10)),
                "a relay command is shown before the camera confirms it")
            check(bool(wait_until(
                lambda: (relay_state(vms, LAMP_RELAY_CAMERA, LAMP_RELAY_TOKEN) or {}).get("livePending")
                is False, 40)),
                "the camera's own report ends the wait")
            check(wait_until(lambda: state_now() == expected, 20),
                  "the control list shows the state the card shows")
        finally:
            set_relay(vms, camera_id(LAMP_RELAY_CAMERA), LAMP_RELAY_TOKEN, bool(before.get("liveActive")))
            wait_until(
                lambda: (relay_state(vms, LAMP_RELAY_CAMERA, LAMP_RELAY_TOKEN) or {}).get("livePending")
                is False, 40)

    # No space at all asks for everything the caller may see — the SQL takes another shape there
    # (no subtree), so it is worth a call of its own.
    everything = control_objects(vms).get("objects") or []
    check(set(row.get("objectId") for row in sensors) <= set(row.get("objectId") for row in everything),
          "an unfiltered page contains the floor's sensors")

    # The map above the floor is the same set through the subtree: the filter is not a canvas id.
    nested = control_objects(vms, site["id"], kind="digital_input").get("objects") or []
    check(sorted(row.get("objectId") for row in nested) == sorted(row.get("objectId") for row in sensors),
          "the map above lists the floor's sensors through the nested space")

    # One device's objects, wherever they stand: the placement palette asks this way.
    owned = control_objects(vms, for_camera=camera_id(ARMED_CAMERA)).get("objects") or []
    check(all(row.get("sourceId") == camera_id(ARMED_CAMERA) for row in owned) and len(owned) >= 2,
          "a camera filter answers with that device's camera and parts")

    nothing = control_objects(vms, str(uuid.uuid4()))
    check(not (nothing.get("objects") or []) and nothing.get("hasMore") is False,
          "a space with no objects is an empty page")

    for bad in ("/api/control/objects?spaceId=not-a-uuid", "/api/control/objects?kind=door"):
        try:
            vms.call("GET", bad)
            check(False, f"{bad} is refused")
        except HttpError as e:
            check(e.status == 400, f"{bad} is refused with 400 (got {e.status})")

    single = control_objects(vms, floor["id"], kind="digital_input", page_size=1)
    first_rows = single.get("objects") or []
    check(len(first_rows) == 1 and single.get("hasMore") is True,
          "a page of one reports that there is more")
    second = control_objects(vms, floor["id"], kind="digital_input", page_size=1,
                             after=single.get("next"))
    second_rows = second.get("objects") or []
    check(len(second_rows) == 1 and second.get("hasMore") is False,
          "the next page takes the other row and ends the list")
    check(first_rows and second_rows
          and first_rows[0].get("objectId") != second_rows[0].get("objectId"),
          "the pages share no row")

    # The inspector's lookup: a node id is the object, and a picture of a camera is still the camera.
    node, parts = device_nodes(vms, ARMED_CAMERA)
    part = next((candidate for candidate in parts if candidate.get("name") == "Master sensor"), None)
    if check(part is not None, "the master sensor has a node of its own"):
        row = control_object(vms, part["id"])
        check(row is not None and row.get("objectId") == part["id"]
              and row.get("kind") == "digital_input",
              "a part's node id resolves to the same object row")
    if node is not None:
        picture = next((child for child in children(vms, floor["id"])
                        if child.get("ownerId") == node["id"]), None)
        if check(picture is not None, "the camera has a picture on the floor"):
            row = control_object(vms, picture["id"])
            check(row is not None and row.get("objectId") == node["id"]
                  and row.get("kind") == "camera",
                  "a picture resolves through its owner to the camera")

    # The panel's camera switch is the same command the monitor card sends.
    all_cameras = control_objects(vms, kind="camera").get("objects") or []
    other = next((row for row in all_cameras if row.get("sourceId") == camera_id("sim-master2")), None)
    if check(other is not None, "sim-master2 has a camera row"):
        before = vms.call("GET", f"/api/cameras/{camera_id('sim-master2')}/state") or {}
        try:
            vms.call("PUT", f"/api/cameras/{camera_id('sim-master2')}/state",
                     {"armed": not before.get("armed")})
            flipped = wait_until(
                lambda: (control_object(vms, other["objectId"]) or {}).get("armed")
                == (not before.get("armed")), 20)
            check(bool(flipped), "arming a camera from its row flips the flag the row shows")
        finally:
            vms.call("PUT", f"/api/cameras/{camera_id('sim-master2')}/state",
                     {"armed": bool(before.get("armed"))})

    # A relay's row follows what the camera reports, not only what was commanded.
    if relays:
        lamp = next((row for row in relays if row.get("partToken") == LAMP_RELAY_TOKEN), None)
        if lamp is not None:
            try:
                set_relay(vms, camera_id(LAMP_RELAY_CAMERA), LAMP_RELAY_TOKEN, True)
                lit = wait_until(
                    lambda: (next((row for row in control_objects(vms, floor["id"], kind="relay")
                                   .get("objects") or []
                                   if row.get("partToken") == LAMP_RELAY_TOKEN), {}) or {})
                    .get("state") == "active", 40)
                check(bool(lit), "a relay row shows the state the camera reports")
            finally:
                set_relay(vms, camera_id(LAMP_RELAY_CAMERA), LAMP_RELAY_TOKEN, False)

    try:
        set_arm_mode(vms, ARMED_CAMERA, ARMED_TOKEN, "armed")
        armed = wait_until(
            lambda: next((row for row in control_objects(vms, floor["id"], kind="digital_input")
                          .get("objects") or []
                          if row.get("sourceId") == camera_id(ARMED_CAMERA)), None), 20)
        check(armed is not None and armed.get("armed") is True
              and armed.get("armingSource") == "explicit",
              "arming from the panel's command turns the row on guard at once")

        set_arm_mode(vms, ARMED_CAMERA, ARMED_TOKEN, "disarmed")
        disarmed = wait_until(
            lambda: next((row for row in control_objects(vms, floor["id"], kind="digital_input")
                          .get("objects") or []
                          if row.get("sourceId") == camera_id(ARMED_CAMERA)
                          and row.get("armed") is False
                          and row.get("armingSource") == "explicit"), None), 20)
        check(disarmed is not None, "disarming turns it off guard, explicitly")
    finally:
        restore = sensor_settings_row(vms, ARMED_CAMERA, ARMED_TOKEN)
        if restore:
            set_device_settings(vms, camera_id(ARMED_CAMERA), {**restore, "armMode": "inherit"})


def place_part_picture(vms, part_id, floor_id, name, lat, lon):
    """A part's replica on a plan, as the administrator places one: the node, its point, its look."""
    picture = vms.call("POST", "/api/map/admin/replicas", {
        "ownerId": part_id,
        "parentId": floor_id,
        "name": name,
    })
    if not picture or not picture.get("id"):
        return None

    vms.call("PUT", "/api/map/admin/figures", {
        "figs": [{
            "id": picture["id"],
            "parentId": floor_id,
            "ownerId": part_id,
            "name": name,
            "geometry": {"type": "Point", "coord": [{"lat": lat, "lon": lon}]},
        }],
    })
    vms.call("PUT", "/api/map/admin/visual", {
        "visuals": [{"objectId": picture["id"], "color": "#26a69a", "text": name}],
    })
    return picture


def check_part_replica(vms, sensor):
    """A part placed on a plan is a replica of the part's node, and the map acts on the part.

    A sensor is placed the way a camera is: a replica under the plan with a point of its own. The
    replica is not a second device — its owner is the part, it carries no camera placement, the
    inspector reads it back as the part, and arming through the picture arms the real input.
    Deleting the picture leaves the part; only the camera's own deletion takes both.
    """
    print("Part replicas on a plan")

    part = sensor["part"]
    floor = next((space for space in spaces(vms) if space.get("name") == BUILDING_NAME), None)
    if not check(floor is not None, f"the demo has the space {BUILDING_NAME!r}"):
        return

    reference = next((p for p in placements(vms)
                      if p.get("cameraId") == camera_id(ARMED_CAMERA)), None)
    lat = (reference or {}).get("lat") or 38.99285
    lon = (reference or {}).get("lon") or -76.93783

    picture = None
    try:
        picture = place_part_picture(vms, part["id"], floor["id"], "Sensor (plan)",
                                     lat + 0.00002, lon + 0.00002)
        if not check(picture is not None, "the plan takes a replica of a part"):
            return

        row = marker(vms, picture["id"])
        check(row is not None and row.get("ownerId") == part["id"] and row.get("nodeKind") == "part",
              "the picture is a part of the device, not a second device")
        check(row.get("parentId") == floor["id"], "the picture stands in the room it was placed in")
        check(not row.get("cameraId"), "a part's picture is not a camera placement")

        through = control_object(vms, picture["id"])
        check(through is not None and through.get("objectId") == part["id"]
              and through.get("kind") == "digital_input",
              "the inspector's read of the picture answers the part")

        set_arm_mode(vms, SENSOR_CAMERA, SENSOR_TOKEN, "disarmed")
        disarmed = wait_until(
            lambda: (control_object(vms, picture["id"]) or {}).get("armed") is False, 20)
        check(bool(disarmed), "the picture shows the sensor it shows going off guard")
        set_arm_mode(vms, SENSOR_CAMERA, SENSOR_TOKEN, "armed")
        armed = wait_until(
            lambda: (control_object(vms, picture["id"]) or {}).get("armed") is True, 20)
        check(bool(armed), "acting on the command turns the sensor on guard")
    finally:
        # sim-ptz's input is installed armed; the step leaves it that way.
        set_arm_mode(vms, SENSOR_CAMERA, SENSOR_TOKEN, "armed")
        if picture and picture.get("id"):
            vms.call("DELETE", "/api/map/admin/objects", {"ids": [picture["id"]]})

    if picture and picture.get("id"):
        check(bool(wait_until(lambda: marker(vms, picture["id"]) is None, 20)),
              "removing the picture leaves the plan")
    check(marker(vms, part["id"]) is not None, "the part's own node is untouched by the picture")


def sensor_settings_row(vms, name, token):
    """The administrator's settings row of one part, as the settings endpoint reports it."""
    return next((row for row in device_settings(vms, camera_id(name))
                 if row.get("kind") == "digital_input" and row.get("token") == token), None)


def check_sensor_in_the_tree(vms):
    """A part is a node of its own, visible under its camera, and placed nowhere."""
    node, parts = device_nodes(vms, SENSOR_CAMERA)
    if not check(node is not None, "sim-ptz has a canonical node in the logical tree"):
        return None

    part = sensor_part(vms)
    if not check(part is not None, "the sensor is a node under the camera"):
        return None

    figures = figures_of(vms, [part["id"]])
    check(not any(f.get("id") == part["id"] and f.get("geometry") for f in figures),
          "the sensor node has no geometry: it is not placed on any map")
    check(not any(p.get("cameraId") == part["id"] for p in placements(vms)),
          "the sensor has no camera placement of its own")

    rows = (vms.call("POST", "/api/map/states", [part["id"]]) or {}).get("states") or []
    check(len(rows) == 1, "the sensor's states are readable")
    check(events_of(vms, part["id"]) is not None, "the sensor's events are readable")

    return {"node": node, "part": part, "ancestors": ancestor_chain(vms, part)}


def settle_sensor(vms, part_id):
    """Wait until the map's copy of the contact matches the wire: open, the idle state.

    A device event may arrive late enough to land after the one that undid it, and then the map
    keeps the older value. The next front would be deduplicated as a repeat of it, so the front
    that needs a transition starts from a state that agrees with the camera.
    """
    return wait_until(
        lambda: (control_object(vms, part_id) or {}).get("state") == "open", 30)


def check_sensor_alarm(vms, sensor):
    """The contact becomes the alarm, and ALARM bubbles up the canonical tree."""
    part = sensor["part"]
    set_relay(vms, camera_id(LAMP_RELAY_CAMERA), "peer", False)
    settle_sensor(vms, part["id"])
    # The journal keeps its rows when a camera is deleted and re-added, so an old `closed` event
    # must not stand in for this front: only an event the command below caused can prove anything.
    before = {event["id"] for event in sensor_events(vms, part["id"], "closed")}
    set_relay(vms, camera_id(LAMP_RELAY_CAMERA), "peer", True)

    event = wait_until(
        lambda: next((e for e in sensor_events(vms, part["id"], "closed")
                      if e["id"] not in before), None), 45)
    check(event is not None, "the peer relay closing writes one `sensor` event with contact=closed")

    # An earlier front (the wired-relay check above) may have left the same alarm open and
    # returned; the rule reactivates that row, and ALARM follows it in its own transaction.
    alarm = wait_until(lambda: active_alarm(vms, part["id"]), 30)
    if not check(alarm is not None, "the armed input raises one active alarm on the sensor node"):
        return None

    check(len([row for row in (alarms(vms).get("open") or {}).get("alarms") or []
               if row.get("objectId") == part["id"]]) == 1,
          "there is exactly one open alarm on the sensor")

    for row in [part] + sensor["ancestors"]:
        wait_until(lambda node=row: alarm_reaches(vms, node["id"]), 20)
        check(alarm_reaches(vms, row["id"]),
              f"ALARM reaches {row.get('name') or row['id']}")

    # The canonical tree ends at the server, but a person reads the room. The camera's reference
    # replica stands on the floor plan, and the alarm is mirrored onto it so the space — and the
    # map above it — counts it among their children.
    floor = next((space for space in spaces(vms) if space.get("name") == BUILDING_NAME), None)
    if check(floor is not None, f"the demo has the room the camera stands in ({BUILDING_NAME!r})"):
        wait_until(lambda: alarm_reaches(vms, floor["id"]), 20)
        check(alarm_reaches(vms, floor["id"]),
              "ALARM reaches the room the camera stands in")

    return alarm


def check_ack_refused(vms, sensor, alarm):
    """Acknowledging an active condition on an armed input is refused, and changes nothing."""
    status = acknowledge(vms, alarm["id"])
    check(status == 409, f"acknowledging while the condition holds answers 409 (got {status})")

    row = close_reason_of(vms, alarm["id"])
    check(row is not None and row.get("status") == "active",
          f"the refusal leaves the alarm active (has {(row or {}).get('status')!r})")


def check_ack_after_disarm(vms, sensor, alarm):
    """Disarming the input is what lets the operator acknowledge a condition that still holds."""
    sensor_row = sensor_settings(vms)
    set_device_settings(vms, camera_id(SENSOR_CAMERA), {
        **sensor_row,
        "armMode": "disarmed",
    })

    # The signal reaches VmsMap through device-settings-changed; a 409 until then is the gate
    # still knowing the old arming, not a failure.
    status = wait_until(lambda: acknowledge(vms, alarm["id"]) == 200, 25)
    check(bool(status), "acknowledging after disarming succeeds")

    row = close_reason_of(vms, alarm["id"])
    check(row is not None and row.get("status") == "acknowledged",
          f"the alarm is acknowledged (has {(row or {}).get('status')!r})")
    check(is_alarmed(vms, sensor["part"]["id"]),
          "acknowledging does not take ALARM away: the condition still holds")
    return row


def check_return_to_normal(vms, sensor, alarm):
    """The contact coming back ends the alarm and takes ALARM off the node and its ancestors."""
    part = sensor["part"]
    before = {event["id"] for event in sensor_events(vms, part["id"], "open")}
    set_relay(vms, camera_id(LAMP_RELAY_CAMERA), "peer", False)

    event = wait_until(
        lambda: next((e for e in sensor_events(vms, part["id"], "open")
                      if e["id"] not in before), None), 45)
    check(event is not None, "the contact returning writes a `sensor` event with contact=open")

    row = wait_until(
        lambda: next((r for r in (alarms(vms).get("closed") or {}).get("alarms") or []
                      if r.get("id") == alarm["id"]), None), 40)
    check(row is not None and row.get("status") in ("returned", "closed"),
          f"the alarm is closed or returned by policy (has {(row or {}).get('status')!r})")
    check(row is None or row.get("conditionActive") is not True,
          "the closed alarm no longer claims an active condition")

    for node in [part] + sensor["ancestors"]:
        check(not alarm_reaches(vms, node["id"]),
              f"ALARM is gone from {node.get('name') or node['id']}")

    floor = next((space for space in spaces(vms) if space.get("name") == BUILDING_NAME), None)
    if floor is not None:
        check(bool(wait_until(lambda: not alarm_reaches(vms, floor["id"]), 20)),
              "ALARM leaves the room when the contact returns")

    check(bool(events_of(vms, part["id"], "alarm_closed")), "`alarm_closed` is in the journal")


def demo_group(vms, since):
    return next((group for group in retention_groups(vms, since)
                 if DEMO_SUBSCRIPTION in (group.get("eventId") or "")), None)


def check_demo_rule(vms, sim, since):
    """One event_id per source node across both cameras, two episodes, no alarm, the lamp lit."""
    group = wait_until(lambda: demo_group(vms, since), 60)
    if not check(group is not None, "the demo rule opened a retention group"):
        return None

    episodes = group.get("episodes") or []
    by_camera = {}
    for episode in episodes:
        by_camera.setdefault(episode.get("cameraId"), []).append(episode)

    for name in (LAMP_RELAY_CAMERA, SENSOR_CAMERA):
        check(camera_id(name) in by_camera,
              f"the demo rule retained {name} under the same event_id")

    uuids = {episode.get("eventUuid") for episode in episodes}
    check(len(uuids) >= 2, "each camera has its own episode (a distinct event_uuid)")

    subscription_alarms = [
        alarm for branch in ("open", "closed")
        for alarm in (alarms(vms).get(branch) or {}).get("alarms") or []
        if alarm.get("source") == "subscription"]
    check(not subscription_alarms, "raise_alarm=false adds no alarm of its own")

    if sim.ready():
        lit = wait_until(lambda: lamp_lit(sim_world(sim), "alert") is True, 20)
        check(bool(lit), "the rule lit the hall lamp")
    else:
        warn("OnvifSim is not answering: skipped the lamp check")

    return group


def check_repeat_front(vms, sensor, group, since):
    """A second front inside MaxEpisodeDuration extends the episodes; it opens no new ones."""
    part = sensor["part"]
    before = {episode.get("cameraId"): episode.get("eventUuid")
              for episode in (group or {}).get("episodes") or []}

    before_fronts = len(sensor_events(vms, part["id"], "closed"))
    set_relay(vms, camera_id(LAMP_RELAY_CAMERA), "peer", True)
    check(bool(wait_until(
        lambda: len(sensor_events(vms, part["id"], "closed")) > before_fronts, 45)),
          "a second front writes a second `sensor` event")

    extended = wait_until(lambda: demo_group(vms, since), 45)
    if check(extended is not None, "the demo rule's group is still there"):
        after = {episode.get("cameraId"): episode.get("eventUuid")
                 for episode in extended.get("episodes") or []}
        for cam, uuid_before in before.items():
            check(after.get(cam) == uuid_before,
                  f"the episode on camera {cam} is extended, not replaced")

    subscription_alarms = [
        alarm for branch in ("open", "closed")
        for alarm in (alarms(vms).get(branch) or {}).get("alarms") or []
        if alarm.get("source") == "subscription"]
    check(not subscription_alarms, "the repeat front raises no subscription alarm")


def check_emit_loop(vms, sensor):
    """A rule that emits what it listens to is stopped by the causation chain, and says so."""
    part = sensor["part"]
    loop_id = str(uuid.uuid4())

    # Back to normal first: the emitted event must be a real transition. The rule is created only
    # now, so the window in which it can fire does not have to cover two device round trips. The
    # wait is for the transition this command causes — an `open` event left over from an earlier
    # scenario would let the relay be set back before the release had reached the device.
    before = {event["id"] for event in sensor_events(vms, part["id"], "open")}
    set_relay(vms, camera_id(LAMP_RELAY_CAMERA), "peer", False)
    wait_until(
        lambda: [event for event in sensor_events(vms, part["id"], "open")
                 if event["id"] not in before], 75)

    vms.call("PUT", "/api/map/admin/event-subscriptions", {"subscription": {
        "id": loop_id,
        "name": "check-emit-loop",
        "enabled": True,
        "eventName": "sensor",
        "sourceFilter": json.dumps({"object_ids": [part["id"]],
                                    "props": {"contact": "closed"}}),
        "armedCondition": "any",
        "cooldownMs": 0,
        "actions": [{
            "position": 0,
            "kind": "emit_event",
            "params": json.dumps({
                "object_id": "source",
                "event_name": "sensor",
                "param0": "closed",
                "param1": "",
                "props": {"contact": "closed"},
            }),
        }],
    }})

    try:
        # Only a transition that happens after the rule is saved can be routed by it. A `closed`
        # event left over from the scenarios above must not make this wait return at once: the
        # device's own delivery is the slow part here, and it is what the 75 s covers.
        before = {event["id"] for event in sensor_events(vms, part["id"], "closed")}
        set_relay(vms, camera_id(LAMP_RELAY_CAMERA), "peer", True)
        closed = wait_until(
            lambda: [event for event in sensor_events(vms, part["id"], "closed")
                     if event["id"] not in before], 75)
        check(bool(closed), "closing the contact writes a new `sensor` event")

        # 30 s after that: the router compares the shared version key on every event, so a lost
        # change signal no longer hides the rule until its cache expires. What is left is the
        # debug-state publication, which ticks every VMS_DEBUG_ENTITY_STATE_SECONDS.
        suppressed = wait_until(
            lambda: next((state for state in entity_states(vms)
                          if state.get("entityType") == "event_routing_suppressed"
                          and loop_id in (state.get("stateJson") or "")), None), 30)
        check(suppressed is not None,
              "the loop is stopped by the chain and reported in the debug registry")
    finally:
        vms.call("DELETE", f"/api/map/admin/event-subscriptions/{loop_id}")


def check_cleanup(vms, sensor):
    """Leaves the world as it was installed: relays off, each sensor back to its own mode."""
    set_relay(vms, camera_id(LAMP_RELAY_CAMERA), "peer", False)
    set_relay(vms, camera_id(LAMP_RELAY_CAMERA), LAMP_RELAY_TOKEN, False)
    set_relay(vms, camera_id(ARMED_CAMERA), ARMED_RELAY_TOKEN, False)

    row = sensor_settings(vms)
    if row:
        set_device_settings(vms, camera_id(SENSOR_CAMERA), {**row, "armMode": "armed"})

    master = sensor_settings_row(vms, ARMED_CAMERA, ARMED_TOKEN)
    if master:
        set_device_settings(vms, camera_id(ARMED_CAMERA), {**master, "armMode": "inherit"})


def check_camera_removal_cascade(vms, sensor):
    """Deleting the camera takes its part nodes and their pictures with it."""
    part_id = sensor["part"]["id"]

    floor = next((space for space in spaces(vms) if space.get("name") == BUILDING_NAME), None)
    picture = place_part_picture(vms, part_id, floor["id"], "Sensor (plan)", 38.99285, -76.93783) \
        if floor is not None else None

    vms.delete_camera(camera_id(SENSOR_CAMERA))

    gone = wait_until(lambda: not map_search(vms, camera_id(SENSOR_CAMERA)), 60)
    check(bool(gone), "deleting the camera removes its nodes from the logical tree")
    if picture and picture.get("id"):
        check(bool(wait_until(lambda: marker(vms, picture["id"]) is None, 30)),
              "deleting the camera removes the pictures of its parts")
    check(not any(alarm.get("objectId") == part_id
                  for branch in ("open", "closed")
                  for alarm in (alarms(vms).get(branch) or {}).get("alarms") or []),
          "no alarm points at the removed sensor")


def ids_of(vms, world):
    ids = {key: world[key]["id"] for key in ("map", "building", "layout", "rack", "switch", "port")}
    ids["layout_racks"] = [rack["id"] for rack in world["layout_racks"]]
    return ids


def check_reinstall(vms, world):
    before = ids_of(vms, world)
    version_before = next((row.get("version") for row in subscriptions(vms)
                           if row.get("id") == DEMO_SUBSCRIPTION), None)

    report = install_demo(vms)
    again = walk_the_world(vms)

    check(again is not None, "the world is still there after a second install")
    if again is None:
        return world

    check(ids_of(vms, again) == before, "every id is the same after a second install")
    check((report.get("created") or 0) == 0, "a second install creates nothing")

    # A reinstall of an unchanged world must not bump the rule's version: every replicable reader
    # would reload its cache for nothing.
    version_after = next((row.get("version") for row in subscriptions(vms)
                          if row.get("id") == DEMO_SUBSCRIPTION), None)
    check(version_after == version_before,
          "the demo rule is not rewritten by a second install")
    return again


def check_user_node_survives(vms, world):
    """The registry names only what the demo wrote; the administrator's node is never touched."""
    user_id = str(uuid.uuid4())
    vms.call("PUT", "/api/map/admin/objects", {
        "objects": [{"id": user_id, "parentId": "", "name": "The administrator's own node"}],
    })

    try:
        install_demo(vms)
        check(vms.call("GET", f"/api/map/objects/{user_id}/props") is not None,
              "a user's node survives an install")

        remove_demo(vms)
        check(vms.call("GET", f"/api/map/objects/{user_id}/props") is not None,
              "a user's node survives a removal")

        # The sensor belongs to the camera, not to the world: removing the world must not take it.
        check(sensor_part(vms) is not None,
              "the demo's device nodes survive the world's removal")

        remaining = [s for s in spaces(vms) if s.get("name") == MAP_NAME]
        check(not remaining, "the demo map is gone after a removal")
        check(not demo_status(vms).get("installed"), "the demo reports itself removed")
    finally:
        vms.call("DELETE", "/api/map/admin/objects", {"ids": [user_id]})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vms", default="http://localhost:5134")
    parser.add_argument("--user", default="admin")
    parser.add_argument("--password", default="admin")
    parser.add_argument("--sim-host", default="127.0.0.1",
                        help="where ta_vms reaches OnvifSim's cameras")
    parser.add_argument("--sim-url", default="http://127.0.0.1:8090",
                        help="OnvifSim's own API, for the lamp and the moving picture")
    parser.add_argument("--server-id", default="vms")
    parser.add_argument("--provision-cameras", action="store_true",
                        help="add the four OnvifSim cameras before installing the world")
    parser.add_argument("--allow-missing-cameras", action="store_true",
                        help="accept a world installed without cameras")
    parser.add_argument("--no-remove-check", action="store_true",
                        help="skip the removal that proves only demo rows go")
    args = parser.parse_args()

    vms = Vms(args.vms, args.user, args.password)
    vms.login()
    sim = Sim(args.sim_url)

    if args.provision_cameras:
        print("Provisioning the demo cameras")
        provision_cameras(vms, args.sim_host, args.server_id)

    # Installing is idempotent, so it is always called first: on a fresh database it writes the
    # world, on an older one it brings it to the current version.
    print("Installing the demo world")
    report = install_demo(vms)
    print(f"  ..    created {report.get('created')}, updated {report.get('updated')}, "
          f"removed {report.get('removed')}, wall {report.get('monitorCameras')}")
    if report.get("skippedCameras"):
        print(f"  ..    cameras not provisioned yet: {report.get('skippedCameras')}")

    # The demo installs the cameras and their calibration; the optional ONVIF event subscription
    # is not its business, so turning it on stays a step of the check.
    subscribe_events(vms)

    print("Walking the world")
    world = walk_the_world(vms)
    if world is None:
        print("\nThe demo world is not in the shape it promises.")
        return 1

    print("Plans")
    check_plans(vms, world)
    check_diagram_marker(vms, world)

    print("Regions and canvases")
    check_positions(vms, world)

    print("Types")
    check_types(vms)

    print("Cameras and the video wall")
    check_cameras(vms, world, args.allow_missing_cameras)
    check_camera_poses(vms, args.allow_missing_cameras)
    check_monitor(vms, args.allow_missing_cameras)

    print("Calibration and the master/slave pairs")
    check_calibration(vms, world, args.allow_missing_cameras)

    print("ONVIF position, events and the marker")
    check_geo_flag(vms)
    check_events(vms)
    check_marker_follows_the_camera(vms)

    print("Layers, the calm port and the camera that rides")
    check_levels(vms)
    check_no_camera_replica(vms)
    check_port_not_alarmed(vms, world)

    sim_up = sim.ready()
    if not sim_up:
        warn(f"OnvifSim is not answering at {args.sim_url}: skipped the lamp and picture checks")
    if sim_up:
        # The device checks below read what the camera last said, and a stack that just started
        # has not been told yet: wait for it rather than reporting a slow first read as a fault.
        for name in (LAMP_RELAY_CAMERA, SENSOR_CAMERA):
            if not wait_until(lambda name=name: device_io_ready(vms, name), 90):
                warn(f"{name}'s device I/O is still not reporting")
        print("The moving camera")
        check_vehicle_picture(sim)
        print("Wired relays and the lamp")
        check_wired_devices(vms, sim)

    # The M2.4e scenario: a real contact, a real alarm, a real subscription. It needs the
    # simulator's wire; without it the map half above still proves the world.
    sensor = None
    if sim_up:
        print("Device I/O, alarms and the demo rule")
        check_provisioning(vms)
        check_operator_arming(vms)
        check_control_panel(vms)
        print("The device's own diagram")
        check_device_diagram(vms, world)
        sensor = check_sensor_in_the_tree(vms)
        if sensor is not None:
            check_part_replica(vms, sensor)
            since = (datetime.now(timezone.utc) - timedelta(minutes=15)).isoformat()
            alarm = check_sensor_alarm(vms, sensor)
            if alarm is not None:
                check_ack_refused(vms, sensor, alarm)
                check_ack_after_disarm(vms, sensor, alarm)
                check_return_to_normal(vms, sensor, alarm)
                group = check_demo_rule(vms, sim, since)
                check_repeat_front(vms, sensor, group, since)
                check_emit_loop(vms, sensor)
            check_cleanup(vms, sensor)
    else:
        warn("OnvifSim is not answering: skipped the device I/O and routing scenario")

    print("Installing again")
    world = check_reinstall(vms, world)

    if not args.no_remove_check:
        print("A user's node beside the world")
        check_user_node_survives(vms, world)
        # The removal above took the world away; put it back for whoever uses the installation next.
        install_demo(vms)

    # Last, because it deletes and re-provisions a camera: the nodes belong to the device, and the
    # world never owns them.
    if sensor is not None and not args.no_remove_check:
        print("Deleting the camera")
        check_camera_removal_cascade(vms, sensor)
        # Put the bench back: the next run starts from the cameras and the world it expects.
        provision_cameras(vms, args.sim_host, args.server_id)
        subscribe_events(vms)
        install_demo(vms)
        if not wait_until(
            lambda: device_io_ready(vms, LAMP_RELAY_CAMERA) and device_io_ready(vms, SENSOR_CAMERA),
            60):
            warn("the re-added camera's device I/O is not reporting yet")

    print()
    if failures:
        print(f"FAILED: {len(failures)} check(s)")
        for failure in failures:
            print(f"  - {failure}")
        return 1

    print("All demo checks passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
