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
MAP_NAME = "map1"
BUILDING_NAME = "plan1"
# The diagram standing on that plan: the same room as a schematic, with the racks drawn in it.
LAYOUT_NAME = "diag1"
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
# The shared canvas the two cameras' rear panels stand on, its bays, the blank diagram the binding
# check uses, and the template the panels are drawn from.
SHARED_CANVAS_NAME = "diag2"
SPARE_NAME = "spare"
SHARED_BAYS = ("left", "right")
CAM_PANEL_TYPE_NAME = "Camera rear panel"


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


def set_relay_until(vms, name, token, active, reached, part_id,
                    input_name, input_token, attempts=2, timeout=30):
    """Switch a relay and wait for what it drives, flipping it back when the map missed the change.

    A camera's configuration change — and the map writes a camera's arming flag on every zone
    change — drops its device subscription; VmsOnvif reopens it on the next read of the camera's
    I/O lists, which is what the operator's panel does every few seconds. A check that reads only
    the map would command a relay nobody is watching, so the list is read first. A transition can
    still be lost, and a repeat of the value the map already holds is not a transition, so the
    retry flips the relay away and back: the next change is one the map can hear. `reached` is what
    proves the action, `part_id` the node whose own state shows the contact for the flip.
    """
    for attempt in range(attempts):
        watch_input(vms, input_name, input_token)
        set_relay(vms, camera_id(name), token, active)
        if wait_until(reached, timeout):
            return True
        if attempt + 1 == attempts:
            return False
        watch_input(vms, input_name, input_token)
        set_relay(vms, camera_id(name), token, not active)
        wait_until(lambda: (control_object(vms, part_id) or {}).get("state")
                   == ("open" if active else "closed"), 20)
    return False


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


def watch_input(vms, name, token):
    """Read an input's list, which is what reopens the camera's device subscription.

    A configuration change — the map writes a camera's arming flag on every zone change — drops
    that subscription, and VmsOnvif opens it again on the next read of the camera's I/O lists,
    which is what the operator's panel polls every few seconds. A step that judges a contact by
    its events must read the list too, or it switches a relay nobody is watching.
    """
    return input_live(vms, name, token)


def digital_input_row(vms, name, token):
    """One input as the operator's card sees it: its mode, its effective arming and the right."""
    rows = vms.call("GET", f"/api/DeviceIo/cameras/{camera_id(name)}/digital-inputs") or []
    return next((candidate for candidate in rows if candidate.get("token") == token), None)


def set_arm_mode(vms, name, token, mode, until=None):
    """The operator's guard switch, as the browser sends it: the mode, and an optional deadline."""
    body = {"armMode": mode}
    if until:
        body["until"] = until
    vms.call("PUT",
             f"/api/DeviceIo/cameras/{camera_id(name)}/digital-inputs/{token}/arm-mode",
             body)


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

    # The shared board stands on the same plan: two bays, each taking a camera's rear panel.
    shared = by_name(vms, floor, SHARED_CANVAS_NAME)
    if not check(shared is not None and shared.get("nodeKind") == "diagram",
                 f"the floor holds the '{SHARED_CANVAS_NAME}' canvas"):
        return None

    # The four racks are regions of the floor's schematic, and every rack is filled with switches.
    # An administrator may draw something else on the same diagram, so the world's racks are what
    # is checked for, not the absence of anything else.
    layout_racks = children(vms, layout["id"])
    expected_racks = {f"rack{index}" for index in range(1, 5)}
    drawn = {r.get("name") for r in layout_racks}
    check(expected_racks <= drawn,
          f"the floor diagram shows rack1 … rack4 (got {sorted(drawn)})")

    rack = by_name(vms, layout_racks, "rack1")
    if not check(rack is not None and rack.get("nodeKind") == "diagram",
                 "the floor diagram holds rack1 as a mnemoscheme"):
        return None

    switches = children(vms, rack["id"])
    check(len(switches) == 6, f"rack1 takes a switch in each of its six slots (got {len(switches)})")

    computer = by_name(vms, switches, "switch1")
    if not check(computer is not None and computer.get("nodeKind") == "diagram",
                 "rack1 holds switch1"):
        return None

    ports = children(vms, computer["id"])
    check(len(ports) == 8, f"switch1 holds 8 ports (got {len(ports)})")
    port = by_name(vms, ports, "port3")
    check(port is not None, "port3 is among them")

    return {
        "map": demo_map, "building": building, "layout": layout,
        "layout_racks": layout_racks, "rack": rack,
        "switch": computer, "port": port, "ports": ports,
        "shared": shared,
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
    for rack in [r for r in world["layout_racks"] if (r.get("name") or "").startswith("rack")]:
        index = int(rack["name"][len("rack"):])
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
    check(port.get("regionId") == "port-3", "port3 sits in the switch's port-3 region")


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
    check(regions == {"input", "lamp", "peer", "loop", "button"},
          f"the panel's five sockets hold a replica each (has {sorted(regions)})")

    peer = next((part for part in parts if part.get("name") == "relay2"), None)
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


# ── The shared board: two cameras' panels on one canvas ──────────────────────────


def shared_panels(vms):
    """The shared canvas, the representation standing in each bay and the blank diagram on it.

    A node listing carries no region — where a node stands inside a diagram is the `diagrams` row's,
    which `by-ids` reads. The floor is looked up by name: a space's id is the server's, so it changes
    when the world is removed and installed again."""
    floor = next((space for space in spaces(vms) if space.get("name") == BUILDING_NAME), None)
    if floor is None:
        return None, {}, None
    canvas = by_name(vms, children(vms, floor["id"]), SHARED_CANVAS_NAME)
    if canvas is None:
        return None, {}, None
    kids = children(vms, canvas["id"])
    shown = [kid for kid in kids if kid.get("ownerId")]
    rows = {row.get("id"): row for row in diagrams_by_ids(vms, [kid["id"] for kid in shown])}
    replicas = {}
    for kid in shown:
        region = (rows.get(kid["id"]) or {}).get("regionId")
        if region:
            replicas[region] = kid
    return canvas, replicas, by_name(vms, kids, SPARE_NAME)


def panel_of(vms, camera):
    """The canonical diagram the map keeps for a camera, under its picture on the floor."""
    floor = next((space for space in spaces(vms) if space.get("name") == BUILDING_NAME), None)
    if floor is None:
        return None
    node, _ = device_nodes(vms, camera)
    picture = next((kid for kid in children(vms, floor["id"])
                    if kid.get("ownerId") == (node or {}).get("id")), None)
    if picture is None:
        return None
    canon = next((kid for kid in children(vms, picture["id"])
                  if kid.get("nodeKind") == "diagram"), None)
    return (canon or {}).get("id")


def diagram_content(vms, node):
    """The diagram's own rows and the replicas directly inside it.

    A representation's children are the canonical diagram's, so they are read by the owner's id; the
    parent node a content read carries for context is not a child and is left out by its own
    parent."""
    content = vms.call("GET", f"/api/map/diagrams/{node['id']}/content?depth=1") or {}
    rows = {row.get("id"): row for row in content.get("content") or []}
    inside = node.get("ownerId") or node.get("id")
    parts = [rows[row.get("id")] for row in content.get("children") or []
             if row.get("id") in rows and row.get("parentId") == inside]
    return rows, parts


def check_shared_canvas(vms):
    """One canvas, two cameras: each bay holds a representation of a camera's canonical diagram, so
    the two panels are one content shown in two places. The paper is the diagram's — a
    representation in a region is a place, not a sheet of its own — and the parts on it are the
    replicas of the camera's configured parts."""
    canvas, replicas, spare = shared_panels(vms)
    if not check(canvas is not None, f"the floor holds the '{SHARED_CANVAS_NAME}' canvas"):
        return

    # The marker on the plan is the card, as for the floor's schematic: the board is entered, not
    # laid over the plan.
    figures = figures_of(vms, [canvas["id"]])
    check(any(figure.get("id") == canvas["id"] for figure in figures),
          "the shared canvas is marked on the plan")

    check(set(replicas) == set(SHARED_BAYS),
          f"one representation stands in each bay (has {sorted(replicas)})")
    check(spare is not None and spare.get("nodeKind") == "diagram"
          and not children(vms, spare["id"]),
          f"the blank '{SPARE_NAME}' diagram waits on the canvas")

    masters = panel_of(vms, "sim-master")
    slave = panel_of(vms, "sim-ptz")
    check((replicas.get("left") or {}).get("ownerId") == masters,
          "the left bay shows the indoor master's canonical diagram")
    check((replicas.get("right") or {}).get("ownerId") == slave,
          "the right bay shows the hall slave's canonical diagram")

    # The panels' configured parts: the master's five sockets, the slave's one input. A device's
    # diagram is drawn only from parts an operator set up.
    expected = {"left": {"input", "lamp", "peer", "loop", "button"}, "right": {"input"}}
    panel_type = next((candidate.get("id") for candidate in diagram_types(vms)
                       if candidate.get("name") == CAM_PANEL_TYPE_NAME), None)
    for bay in SHARED_BAYS:
        replica = replicas.get(bay)
        if not check(replica is not None, f"a representation stands in the {bay} bay"):
            continue
        rows, parts = diagram_content(vms, replica)
        root = rows.get(replica["id"]) or {}
        check(root.get("isRepresentation") is True,
              f"the {bay} bay is a representation, not a diagram of its own")
        check(root.get("dgrType") == panel_type,
              f"the {bay} bay opens on the panel's type")
        check(not root.get("placement"),
              f"a representation in a region carries no rectangle of its own ({bay})")
        check({row.get("regionId") for row in parts} == expected[bay],
              f"the {bay} bay shows the camera's configured parts "
              f"(has {sorted(row.get('regionId') for row in parts)})")


def check_shared_panel_is_live(vms):
    """The panel on the board is live: a relay closed on the camera lights its socket inside the
    representation, because a replica shows its owner's state rather than keeping one."""
    _, replicas, _ = shared_panels(vms)
    left = replicas.get("left")
    if not check(left is not None, "the master's panel stands in the left bay"):
        return

    _, parts = diagram_content(vms, left)
    socket = next((row for row in parts if row.get("regionId") == "peer"), None)
    if not check(socket is not None, "the left bay shows the peer socket"):
        return

    set_relay(vms, camera_id("sim-master"), "peer", True)
    lit = wait_until(
        lambda: "RELAY_ACTIVE" in (states_of(vms, [socket["id"]]).get(socket["id"]) or []), 40)
    set_relay(vms, camera_id("sim-master"), "peer", False)
    check(lit, "closing peer lights the socket inside the representation")


def device_binding(vms, node_id):
    return vms.call("GET", f"/api/map/admin/device-diagram/binding?nodeId={node_id}")


def bind_diagram(vms, node_id, camera):
    return vms.call("POST", "/api/map/admin/device-diagram/bind", {
        "nodeId": node_id, "sourceKind": "camera", "sourceId": camera})


def unbind_diagram(vms, node_id):
    return vms.call("DELETE", "/api/map/admin/device-diagram/bind", {"nodeId": node_id})


def check_shared_diagram_binding(vms):
    """A drawn diagram can be bound to a device: the blank `spare` takes the slave's part and gives
    it back. A device keeps one diagram per view, so the world's own panel is unbound first and put
    back at the end — and the board keeps showing it meanwhile, as an ordinary diagram."""
    _, replicas, spare = shared_panels(vms)
    canon = (replicas.get("right") or {}).get("ownerId")
    if not check(spare is not None and canon is not None,
                 "the canvas holds the slave's panel and the blank diagram"):
        return
    camera = camera_id("sim-ptz")

    refused = False
    try:
        bind_diagram(vms, spare["id"], camera)
    except HttpError as error:
        # A device that already has a diagram for the view is the map's state refusing the
        # binding, not a malformed request: 409.
        refused = error.status == 409
    check(refused, "a device keeps one diagram: binding a second one is refused, and says so")

    check(unbind_diagram(vms, canon).get("success") is True, "the world's panel is unbound")
    check(device_binding(vms, canon).get("bound") is not True,
          "the unbound panel is an ordinary diagram")
    rows, parts = diagram_content(vms, replicas["right"])
    check((rows.get(replicas["right"]["id"]) or {}).get("isRepresentation") is True,
          "its representation keeps showing it")
    check(not parts, "an unbound diagram draws no managed replica")

    bind_diagram(vms, spare["id"], camera)
    check(device_binding(vms, spare["id"]).get("sourceId") == camera,
          "the drawn diagram is bound to the slave")
    rows, parts = diagram_content(vms, spare)
    check({row.get("regionId") for row in parts} == {"input"},
          "the bound diagram takes the slave's configured part")

    unbind_diagram(vms, spare["id"])
    check(marker(vms, spare["id"]) is not None and not children(vms, spare["id"]),
          "unbinding leaves the diagram, empty again")
    rows, _ = diagram_content(vms, spare)
    check((rows.get(spare["id"]) or {}).get("dgrType") is not None,
          "and the diagram keeps its type")

    bind_diagram(vms, canon, camera)
    rows, parts = diagram_content(vms, replicas["right"])
    check({row.get("regionId") for row in parts} == {"input"},
          "the world's panel is back on the board, with its part")


def check_shared_canvas_removal(vms, world):
    """Removing the canvas takes its representations and leaves the canonical diagrams: the board
    is one place a diagram is shown, not the diagram itself. A reinstall puts the board back."""
    canvas, replicas, _ = shared_panels(vms)
    canons = [replica.get("ownerId") for replica in replicas.values()]
    if not check(canvas is not None and len(canons) == len(SHARED_BAYS),
                 "the board and its two representations exist"):
        return world

    vms.call("DELETE", "/api/map/admin/objects", {"ids": [canvas["id"]]})
    gone = wait_until(
        lambda: marker(vms, canvas["id"]) is None
        and all(marker(vms, (replica or {}).get("id")) is None for replica in replicas.values()),
        60)
    check(gone, "deleting the canvas takes both representations")
    for canon in canons:
        check(marker(vms, canon) is not None, "a canonical diagram outlives the canvas it stood on")

    install_demo(vms)
    again = walk_the_world(vms)
    if again is None:
        return world
    canvas, replicas, _ = shared_panels(vms)
    check(set(replicas) == set(SHARED_BAYS),
          "a reinstall draws the board again, with a panel in each bay")
    return again


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
    check({"input", "lamp", "peer", "loop", "button"} == panel_regions,
          f"Camera rear panel offers its five sockets (has {sorted(panel_regions)})")
    check(panel.get("sourceKind") == "camera" and panel.get("autoAttach") is True,
          "the panel is a template that attaches itself to its model")
    bindings = {r.get("regionKey"): (r.get("part") or {}) for r in panel.get("regions") or []}
    check(bindings.get("peer", {}).get("matchValue") == "peer",
          "the peer socket finds its part by token")

    shared = types.get("Cam diagrams", {})
    shared_regions = {r.get("regionKey") for r in shared.get("regions") or []}
    check(shared_regions == set(SHARED_BAYS),
          f"Cam diagrams offers its two bays (has {sorted(shared_regions)})")
    check(not any(r.get("part") for r in shared.get("regions") or []),
          "a bay is a place on the board, not a slot of a device part")


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
    check("CONNECTED" in states, f"port3 is CONNECTED (has {states})")
    check(not {"DISCONNECTED", "ALARM"} & set(states),
          f"port3 carries no alarm state (has {states})")

    alarmed_ids = {row.get("id") for row in alarmed(vms)}
    check(port_id not in alarmed_ids, "port3 is not among the alarmed nodes")


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
        return next((part for part in parts if part.get("name") == "sensor2"), None)

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
    part = next((candidate for candidate in parts if candidate.get("name") == "sensor1"), None)
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
    sim-master's four relays. The check proves the server filters by space, kind and camera,
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
    check(sorted(row.get("name") for row in sensors) == ["sensor1", "sensor2"],
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
    part = next((candidate for candidate in parts if candidate.get("name") == "sensor1"), None)
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
        cid = camera_id('sim-master2')
        before = camera_arm_state(vms, cid)
        try:
            # A command from the panel is an explicit decision: the mode becomes armed/disarmed.
            vms.call("PUT", f"/api/cameras/{cid}/state", {"armed": not before.get("armed")})
            flipped = wait_until(
                lambda: (control_object(vms, other["objectId"]) or {}).get("armed")
                == (not before.get("armed")), 20)
            check(bool(flipped), "arming a camera from its row flips the flag the row shows")
            decided = camera_arm_state(vms, cid)
            check(decided.get("armMode") == ("armed" if decided.get("armed") else "disarmed"),
                  f"the command is explicit (has {decided.get('armMode')!r})")
        finally:
            vms.call("PUT", f"/api/cameras/{cid}/arm-mode",
                     {"armMode": before.get("armMode") or "inherit"})

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


def check_map_relay(vms):
    """The master's fourth relay stands on the map itself, and the operator switches it there.

    The picture is the world's own, not an administrator's: a replica of the part's node, placed on
    the map by the demo and named by the server. Acting on it acts on the relay, and that relay
    closes the same sensor `relay3` does — two relays, one input, one alarm.
    """
    print("The relay on the map")

    site = next((space for space in spaces(vms) if space.get("name") == MAP_NAME), None)
    floor = next((space for space in spaces(vms) if space.get("name") == BUILDING_NAME), None)
    if not check(site is not None and floor is not None, "the demo has its map and its plan"):
        return

    _, parts = device_nodes(vms, ARMED_CAMERA)
    relay = next((candidate for candidate in parts if candidate.get("name") == "relay4"), None)
    sensor = next((candidate for candidate in parts if candidate.get("name") == "sensor1"), None)
    if not check(relay is not None and sensor is not None,
                 "sim-master carries relay4 and its sensor"):
        return

    picture = next((kid for kid in children(vms, site["id"])
                    if kid.get("ownerId") == relay["id"]), None)
    if not check(picture is not None, "the world places relay4 on the map as a replica"):
        return
    check(picture.get("name") == "relay4 rep1",
          f"the picture carries the relay's name and its own number (has {picture.get('name')!r})")
    check(picture.get("parentId") == site["id"] and not picture.get("cameraId"),
          "a part's picture is not a camera placement")
    check(picture.get("nodeKind") == "part", "the map reads the picture as part of the device")

    through = control_object(vms, picture["id"])
    check(through is not None and through.get("objectId") == relay["id"]
          and through.get("kind") == "relay" and through.get("partToken") == "button"
          and through.get("sourceId") == camera_id(ARMED_CAMERA),
          "the inspector's read of the picture answers the relay")
    check(bool(through and through.get("canControl")),
          "the operator may act on the relay from the map")

    # A picture dropped without a name is named by the server: the owner and the next free number.
    # The world's own picture already took `rep1`.
    unnamed = vms.call("POST", "/api/map/admin/replicas",
                       {"ownerId": relay["id"], "parentId": floor["id"]}) or {}
    check(unnamed.get("name") == "relay4 rep2",
          f"an unnamed picture is named by its owner and the next number (has {unnamed.get('name')!r})")
    if unnamed.get("id"):
        vms.call("DELETE", "/api/map/admin/objects", {"ids": [unnamed["id"]]})

    def switch(token, active):
        set_relay(vms, camera_id(ARMED_CAMERA), token, active)

    try:
        set_arm_mode(vms, ARMED_CAMERA, ARMED_TOKEN, "armed")
        switch("button", False)
        switch(ARMED_RELAY_TOKEN, False)
        settle_sensor(vms, sensor["id"])
        armed = wait_until(
            lambda: (digital_input_row(vms, ARMED_CAMERA, ARMED_TOKEN) or {}).get("armed") is True, 30)
        if not check(bool(armed), "the operator's sensor is on guard"):
            return

        switch("button", True)
        check(bool(wait_until(lambda: input_live(vms, ARMED_CAMERA, ARMED_TOKEN) is True, 40)),
              "switching relay4 closes the sensor")
        check(wait_until(lambda: active_alarm(vms, sensor["id"]), 30) is not None,
              "the closed sensor raises the alarm the map shows")

        switch("button", False)
        check(bool(wait_until(lambda: input_live(vms, ARMED_CAMERA, ARMED_TOKEN) is False, 40)),
              "switching it back opens the sensor again")
        check(bool(wait_until(lambda: active_alarm(vms, sensor["id"]) is None, 40)),
              "the alarm returns when the contact does")

        # The relay the demo already had reaches the same input: two relays, one sensor.
        switch(ARMED_RELAY_TOKEN, True)
        check(bool(wait_until(lambda: input_live(vms, ARMED_CAMERA, ARMED_TOKEN) is True, 40)),
              "relay3 closes the same sensor relay4 closed")
        check(wait_until(lambda: active_alarm(vms, sensor["id"]), 30) is not None,
              "and raises the same alarm")
        switch(ARMED_RELAY_TOKEN, False)
        check(bool(wait_until(lambda: active_alarm(vms, sensor["id"]) is None, 40)),
              "opening relay3 returns it")
    finally:
        switch("button", False)
        switch(ARMED_RELAY_TOKEN, False)
        set_arm_mode(vms, ARMED_CAMERA, ARMED_TOKEN, "inherit")


def node_arming(vms, object_id):
    """One node's guard state, as the operator's guard switch asks for it."""
    try:
        return vms.call("GET", f"/api/control/objects/{object_id}/arming")
    except HttpError as e:
        if e.status == 404:
            return None
        raise


def set_node_arming(vms, object_id, mode, until=None):
    """The one write behind every guard switch; answers what the node resolves to afterwards."""
    body = {"mode": mode}
    if until:
        body["until"] = until
    return vms.call("PUT", f"/api/control/objects/{object_id}/arming", body)


def acknowledge_zone(vms, object_id):
    """Accepting a zone: every open alarm of the devices under it, with a result per alarm."""
    return vms.call("POST", f"/api/control/objects/{object_id}/alarms/acknowledge",
                    {"comment": "demo"})


def node_props(vms, object_id):
    return vms.call("GET", f"/api/map/objects/{object_id}/props") or {}


def check_zone_arming(vms):
    """Arming a space: one row on the floor's node, and every sensor under it answers.

    The zone is not written onto the sensors — it is resolved for them — so this also proves the
    absence of a mirror, that an alarm names the zone it belongs to, and that disarming the zone
    keeps the alarm while letting the operator accept it.
    """
    print("Zone arming")

    floor = next((space for space in spaces(vms) if space.get("name") == BUILDING_NAME), None)
    if not check(floor is not None, f"the demo has the space {BUILDING_NAME!r}"):
        return

    node, parts = device_nodes(vms, ARMED_CAMERA)
    part = next((candidate for candidate in parts if candidate.get("name") == "sensor1"), None)
    if not check(part is not None, "the master sensor is a node under the camera"):
        return

    def row():
        return digital_input_row(vms, ARMED_CAMERA, ARMED_TOKEN)

    # The previous step put the input back to its own camera's arming; the zone starts unset.
    if not check(bool(wait_until(lambda: (row() or {}).get("armingSource") == "camera", 30)),
                 "the master sensor starts following its camera, not a zone"):
        return

    initial = node_arming(vms, floor["id"])
    if not check(initial is not None, "a space answers an arming read"):
        return
    check(initial.get("armable") is True, "a space a camera stands in may be armed")
    check(initial.get("configured") is True, "a space needs no settings row to be armable")
    check(initial.get("mode") == "inherit",
          f"the floor starts following its parent (has {initial.get('mode')!r})")
    check(initial.get("armed") is False, "the floor is not on guard")

    set_relay(vms, camera_id(ARMED_CAMERA), ARMED_RELAY_TOKEN, False)
    settle_sensor(vms, part["id"])

    try:
        armed = set_node_arming(vms, floor["id"], "armed")
        check(armed.get("mode") == "armed" and armed.get("armed") is True,
              "arming the space writes its mode and answers the effective state")
        check(armed.get("source") == "explicit", "the answer says the floor itself is armed")

        follows = wait_until(
            lambda: (row() or {}).get("armed") is True
            and (row() or {}).get("armingSource") == "space", 30)
        check(bool(follows), "the sensor under the floor is on guard through the space")

        # The zone is resolved, not propagated: no property is written to the sensor's node.
        props = node_props(vms, part["id"]).get("extraProps") or []
        check(not any((p.get("propName") or "") == "armed" for p in props),
              "the sensor's own node carries no armed property (no mirror)")

        before = {event["id"] for event in sensor_events(vms, part["id"], "closed")}
        set_relay(vms, camera_id(ARMED_CAMERA), ARMED_RELAY_TOKEN, True)
        wait_until(lambda: input_live(vms, ARMED_CAMERA, ARMED_TOKEN) is True, 40)
        alarm = wait_until(lambda: active_alarm(vms, part["id"]), 40)
        check(alarm is not None, "a contact that closes under an armed floor raises one alarm")
        if alarm is not None:
            check(alarm.get("armingSourceObjectId") == floor["id"],
                  "the alarm names the zone its device is guarded by")
            # The arming no longer refuses the acknowledgement: it asks for a comment
            # (M2.4j), which is the refusal `check_ack_refused` meets below.
            check(alarm.get("canAck") is True and alarm.get("ackRequiresComment") is True,
                  "acknowledging under an armed floor asks for a comment")

            disarmed = set_node_arming(vms, floor["id"], "disarmed")
            check(disarmed.get("armed") is False,
                  "disarming the floor resolves its sensors off guard")
            check(active_alarm(vms, part["id"]) is not None,
                  "disarming closes no alarm: only the condition leaving does")

            accepted = acknowledge_zone(vms, floor["id"])
            check(accepted.get("acknowledged", 0) >= 1,
                  f"accepting the zone acknowledges its open alarm (got {accepted!r})")
            check(not any(r.get("reason") == "armed" for r in accepted.get("rejected") or []),
                  "nothing is refused once the zone is off guard")

        journal = [event for event in events_of(vms, floor["id"], "arming")
                   if event.get("param0") and event.get("param1") == "armed"]
        check(bool(journal), "the journal records who armed the floor and with which mode")
        # By id, not by count: the filter answers a page, so on a stack with a long history a new
        # event pushes an old one out and the count would not move.
        check(any(event["id"] not in before
                  for event in sensor_events(vms, part["id"], "closed")),
              "a new `sensor` front closed the contact")
    finally:
        set_node_arming(vms, floor["id"], "inherit")
        set_relay(vms, camera_id(ARMED_CAMERA), ARMED_RELAY_TOKEN, False)
        wait_until(lambda: input_live(vms, ARMED_CAMERA, ARMED_TOKEN) is False, 40)
        wait_until(lambda: active_alarm(vms, part["id"]) is None, 40)


def check_zone_bypass(vms):
    """A timed exclusion: the node stays off until its deadline, then returns by itself.

    The sweep runs in the stack, so the step waits for the deadline instead of moving a clock. It
    also proves the zone-level case: one write on the floor excludes every sensor that follows it,
    without touching each sensor's row.
    """
    print("Zone bypass")

    floor = next((space for space in spaces(vms) if space.get("name") == BUILDING_NAME), None)
    site = next((space for space in spaces(vms) if space.get("name") == MAP_NAME), None)
    if not check(floor is not None and site is not None, "the demo has the floor and the site"):
        return

    _, parts = device_nodes(vms, ARMED_CAMERA)
    part = next((candidate for candidate in parts if candidate.get("name") == "sensor1"), None)
    if not check(part is not None, "the master sensor is a node under the camera"):
        return

    def row():
        return digital_input_row(vms, ARMED_CAMERA, ARMED_TOKEN)

    set_relay(vms, camera_id(ARMED_CAMERA), ARMED_RELAY_TOKEN, False)
    settle_sensor(vms, part["id"])

    try:
        set_node_arming(vms, floor["id"], "armed")
        check(bool(wait_until(lambda: (row() or {}).get("armed") is True, 30)),
              "the floor guards the sensor before the bypass")

        until = (datetime.now(timezone.utc) + timedelta(seconds=10)).isoformat()
        timed = set_node_arming(vms, part["id"], "disarmed", until)
        check(timed.get("bypassed") is True,
              f"a timed disarm under an armed floor is a bypass (got {timed!r})")
        check(timed.get("sourceObjectId") == floor["id"],
              "the bypass names the zone it is excluded from")
        check(bool(timed.get("bypassUntil")), "the answer carries the deadline")
        check((row() or {}).get("bypassed") is True,
              "the control list shows the same bypass")

        check(bool(set_relay_until(
                  vms, ARMED_CAMERA, ARMED_RELAY_TOKEN, True,
                  lambda: (control_object(vms, part["id"]) or {}).get("state") == "closed",
                  part["id"], ARMED_CAMERA, ARMED_TOKEN)),
              "the contact closes and the map knows it")
        time.sleep(4)
        check(active_alarm(vms, part["id"]) is None,
              "a contact closing while bypassed raises no alarm")

        check(bool(wait_until(
                  lambda: (node_arming(vms, part["id"]) or {}).get("bypassed") is False, 90)),
              "the deadline returns the sensor to its zone by itself")
        check(bool(wait_until(lambda: active_alarm(vms, part["id"]) is not None, 40)),
              "the contact still closed raises the moment the bypass ends")

        expiry = next((event for event in events_of(vms, part["id"], "arming")
                       if event.get("param0") == "system"), None)
        check(expiry is not None, "the journal records the automatic return by `system`")
        check(any((p.get("propName") or "") == "expired"
                  for p in (expiry or {}).get("extraProps") or []),
              "the journal entry says it expired")

        # Clear the contact, then exclude the whole room with one write on the floor.
        check(bool(set_relay_until(
                  vms, ARMED_CAMERA, ARMED_RELAY_TOKEN, False,
                  lambda: active_alarm(vms, part["id"]) is None,
                  part["id"], ARMED_CAMERA, ARMED_TOKEN)),
              "opening the loop returns the contact to normal")

        set_node_arming(vms, site["id"], "armed")
        until = (datetime.now(timezone.utc) + timedelta(seconds=10)).isoformat()
        floor_bypass = set_node_arming(vms, floor["id"], "disarmed", until)
        check(floor_bypass.get("bypassed") is True,
              "the floor itself is bypassed inside the armed site")
        check(bool(wait_until(lambda: (row() or {}).get("armed") is False, 30)),
              "the inheriting sensor follows the bypassed floor")

        check(bool(set_relay_until(
                  vms, ARMED_CAMERA, ARMED_RELAY_TOKEN, True,
                  lambda: (control_object(vms, part["id"]) or {}).get("state") == "closed",
                  part["id"], ARMED_CAMERA, ARMED_TOKEN)),
              "the excluded room's contact still reaches the map")
        time.sleep(4)
        check(active_alarm(vms, part["id"]) is None,
              "the whole room is excluded, so its sensor raises nothing")

        check(bool(wait_until(lambda: (row() or {}).get("armed") is True, 90)),
              "the floor comes back by itself")
        check(bool(wait_until(lambda: active_alarm(vms, part["id"]) is not None, 40)),
              "the still-closed contact raises when the room comes back")
    finally:
        set_node_arming(vms, part["id"], "inherit")
        set_node_arming(vms, floor["id"], "inherit")
        set_node_arming(vms, site["id"], "inherit")
        set_relay(vms, camera_id(ARMED_CAMERA), ARMED_RELAY_TOKEN, False)
        wait_until(lambda: input_live(vms, ARMED_CAMERA, ARMED_TOKEN) is False, 40)
        wait_until(lambda: active_alarm(vms, part["id"]) is None, 40)


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
    watch_input(vms, SENSOR_CAMERA, SENSOR_TOKEN)
    set_relay(vms, camera_id(LAMP_RELAY_CAMERA), "peer", False)
    settle_sensor(vms, part["id"])
    # The journal keeps its rows when a camera is deleted and re-added, so an old `closed` event
    # must not stand in for this front: only an event the command below caused can prove anything.
    before = {event["id"] for event in sensor_events(vms, part["id"], "closed")}

    def new_closed():
        return next((e for e in sensor_events(vms, part["id"], "closed")
                     if e["id"] not in before), None)

    raised = set_relay_until(vms, LAMP_RELAY_CAMERA, "peer", True, new_closed, part["id"],
                             SENSOR_CAMERA, SENSOR_TOKEN)
    check(bool(raised),
          "the peer relay closing writes one `sensor` event with contact=closed")

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
    watch_input(vms, SENSOR_CAMERA, SENSOR_TOKEN)
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
    watch_input(vms, SENSOR_CAMERA, SENSOR_TOKEN)
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
    watch_input(vms, SENSOR_CAMERA, SENSOR_TOKEN)
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
        watch_input(vms, SENSOR_CAMERA, SENSOR_TOKEN)
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


def reset_zone_arming(vms):
    """The run starts from the world's own arming, not from a hand that used the panel earlier.

    A demo space left armed makes every sensor under it answer through the zone, so the checks
    about a sensor's own scheme (a camera's flag, an explicit mode) fail for a reason that is not
    a defect. Only the world's spaces are touched: a space of the operator's own is theirs.
    """
    for space in spaces(vms):
        if space.get("name") not in (MAP_NAME, BUILDING_NAME) or not space.get("id"):
            continue
        state = node_arming(vms, space["id"])
        if state and state.get("mode") != "inherit":
            set_node_arming(vms, space["id"], "inherit")


def camera_arm_state(vms, cid):
    """The camera's own mode and its effective flag. The mode lives in VmsMap now, so it is read
    from the settings endpoint rather than from the live state's flag alone."""
    return vms.call("GET", f"/api/DeviceIo/cameras/{cid}/device-settings?partKind=device") or {}


def set_camera_arm_mode(vms, cid, mode):
    vms.call("PUT", f"/api/cameras/{cid}/arm-mode", {"armMode": mode})


def check_camera_zone_arming(vms):
    """A camera is armed the way a sensor is: its own mode, else the zone it stands in, else off.

    The flag VmsOnvif and the routing gateway read is derived by the map, so this proves that write
    lands too — and that the zone is not copied onto the camera: an explicit 'not on guard' still
    wins while the space it stands in is armed.
    """
    print("Camera zone arming")

    floor = next((space for space in spaces(vms) if space.get("name") == BUILDING_NAME), None)
    if not check(floor is not None, f"the demo has the space {BUILDING_NAME!r}"):
        return

    camera = camera_id(ARMED_CAMERA)
    node, _ = device_nodes(vms, ARMED_CAMERA)
    if not check(node is not None, "the camera has a node in the logical tree"):
        return

    before = camera_arm_state(vms, camera)
    try:
        set_camera_arm_mode(vms, camera, "inherit")
        check(bool(wait_until(
            lambda: camera_arm_state(vms, camera).get("armMode") == "inherit", 30)),
            "the camera follows its zone")
        check(bool(wait_until(
            lambda: camera_arm_state(vms, camera).get("armed") is False, 30)),
            "with no space armed the derived flag is off")

        set_node_arming(vms, floor["id"], "armed")
        on_guard = wait_until(
            lambda: camera_arm_state(vms, camera).get("armed") is True
            and camera_arm_state(vms, camera).get("armMode") == "inherit", 40)
        check(bool(on_guard),
              "a camera standing in an armed space is on guard, and still inherits")

        def row():
            return control_object(vms, node["id"]) or {}

        guarded = wait_until(
            lambda: row().get("armed") is True and row().get("armingSource") == "space", 30)
        check(bool(guarded), "the camera's row says it is guarded through the space")
        check(row().get("armingSourceObjectId") == floor["id"],
              "the row names the space that settled it")

        # An explicit command is the operator's: the armed space does not override it.
        vms.call("PUT", f"/api/cameras/{camera}/state", {"armed": False})
        explicit = wait_until(
            lambda: row().get("armed") is False and row().get("armingSource") == "explicit", 30)
        check(bool(explicit), "an explicit 'not on guard' beats the armed space")
        state = camera_arm_state(vms, camera)
        check(state.get("armMode") == "disarmed",
              f"and the command is explicit (has {state.get('armMode')!r})")

        # A deadline on the camera's own command: it is a bypass of the armed space while it
        # lasts, and the sweep hands the camera back to the space, not to the off it replaced.
        until = (datetime.now(timezone.utc) + timedelta(seconds=10)).isoformat()
        timed = set_node_arming(vms, node["id"], "disarmed", until)
        check(timed.get("bypassed") is True,
              "a timed off on the camera is a bypass of the space")
        check(timed.get("sourceObjectId") == floor["id"],
              "the camera's bypass names the space it is excluded from")
        check(bool(wait_until(lambda: row().get("bypassed") is False, 90)),
              "the camera's deadline hands it back to the space")
        check(bool(wait_until(
                  lambda: camera_arm_state(vms, camera).get("armMode") == "inherit", 30)),
              "and the sweep restored `inherit`, not the off it replaced")
    finally:
        set_camera_arm_mode(vms, camera, before.get("armMode") or "inherit")
        set_node_arming(vms, floor["id"], "inherit")


def check_cleanup(vms, sensor):
    """Leaves the world as it was installed: relays off, each sensor back to its own mode."""
    set_relay(vms, camera_id(LAMP_RELAY_CAMERA), "peer", False)
    set_relay(vms, camera_id(LAMP_RELAY_CAMERA), LAMP_RELAY_TOKEN, False)
    set_relay(vms, camera_id(ARMED_CAMERA), ARMED_RELAY_TOKEN, False)

    # A run that died inside the zone step leaves its floor armed; the property is not the demo's
    # and nothing else would clear it.
    floor = next((space for space in spaces(vms) if space.get("name") == BUILDING_NAME), None)
    if floor is not None and floor.get("id"):
        try:
            set_node_arming(vms, floor["id"], "inherit")
        except HttpError:
            pass

    row = sensor_settings(vms)
    if row:
        set_device_settings(vms, camera_id(SENSOR_CAMERA), {**row, "armMode": "armed"})

    master = sensor_settings_row(vms, ARMED_CAMERA, ARMED_TOKEN)
    if master:
        set_device_settings(vms, camera_id(ARMED_CAMERA), {**master, "armMode": "inherit"})


def check_camera_removal_cascade(vms, sensor):
    """Deleting the camera takes its part nodes and their pictures with it — and its panel on the
    shared board, one place that panel is shown — while the other camera's panel keeps standing."""
    part_id = sensor["part"]["id"]

    _, replicas, _ = shared_panels(vms)
    left = (replicas.get("left") or {}).get("ownerId")
    right = (replicas.get("right") or {}).get("ownerId")

    floor = next((space for space in spaces(vms) if space.get("name") == BUILDING_NAME), None)
    picture = place_part_picture(vms, part_id, floor["id"], "Sensor (plan)", 38.99285, -76.93783) \
        if floor is not None else None

    vms.delete_camera(camera_id(SENSOR_CAMERA))

    gone = wait_until(lambda: not map_search(vms, camera_id(SENSOR_CAMERA)), 60)
    check(bool(gone), "deleting the camera removes its nodes from the logical tree")
    if picture and picture.get("id"):
        check(bool(wait_until(lambda: marker(vms, picture["id"]) is None, 30)),
              "deleting the camera removes the pictures of its parts")
    check(left is not None and marker(vms, left) is not None,
          "the other camera's panel is untouched by the removal")
    check(right is not None and bool(wait_until(lambda: marker(vms, right) is None, 30)),
          "the removed camera's own panel goes with it")
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
    reset_zone_arming(vms)
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
        check_map_relay(vms)
        check_zone_arming(vms)
        check_zone_bypass(vms)
        check_camera_zone_arming(vms)
        print("The device's own diagram")
        check_device_diagram(vms, world)
        print("The shared board")
        check_shared_canvas(vms)
        check_shared_panel_is_live(vms)
        check_shared_diagram_binding(vms)
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

    if not args.no_remove_check:
        print("Removing the shared canvas")
        world = check_shared_canvas_removal(vms, world)

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
