#!/usr/bin/env python3
"""End-to-end check of the demo world through the gateway.

The world is installed through the same API the administrator's button uses, then walked the way
an operator walks it: map -> building -> room -> rack -> switch -> port, with the types and their
regions, the camera placements, the video wall and the alarm climbing back up. It then proves what
the registry is for — a second install changes no id, the administrator's own node survives both
an install and a removal, and a removal takes only the demo.

    tests/demo/check_demo.py                    # install if needed, then check
    tests/demo/check_demo.py --provision-cameras  # also add the OnvifSim cameras first
    tests/demo/check_demo.py --keep-installed     # leave the world installed (the default)
"""

import argparse
import sys
import time
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "onvifsim"))

from labhttp import HttpError  # noqa: E402
from vmsapi import Vms  # noqa: E402

DEMO_KEY = "umd-datacenter"
MAP_NAME = "UMD Data Center (demo)"
BUILDING_NAME = "A.V. Williams Building"
ROOM_PLAN_NAME = "Room 101"
ROOM_DIAGRAM_NAME = "Room 102"
DEMO_CAMERAS = ["sim-master", "sim-master2", "sim-ptz", "sim-ptz2"]

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


def values_of(vms, owner_id):
    return (vms.call("POST", "/api/map/values/by-owners", [owner_id]) or {}).get("values") or []


def levels(vms):
    return (vms.call("GET", "/api/map/levels") or {}).get("levels") or []


def placements(vms):
    return vms.call("GET", "/api/spatial/placements") or []


def monitors(vms):
    return vms.call("GET", "/api/monitor") or []


# ── Provisioning ──────────────────────────────────────────────────────────────────


def provision_cameras(vms, sim_host, server_id):
    """The four OnvifSim cameras, added the way the bench's provision step adds them."""
    existing = {camera.get("cameraId") for camera in vms.cameras()}
    wanted = [("sim-master", 8101, False), ("sim-ptz", 8102, True),
              ("sim-master2", 8103, False), ("sim-ptz2", 8104, True)]

    for camera_id, port, telemetry in wanted:
        if camera_id in existing:
            warn(f"camera {camera_id} is already configured")
            continue

        vms.add_camera({
            "cameraId": camera_id,
            "name": f"OnvifSim {camera_id}",
            "serverId": server_id,
            "onvifConn": {"ip": sim_host, "port": port,
                          "xaddr": f"http://{sim_host}:{port}/onvif/device_service"},
            "cred": {"user": "admin", "pass": "admin"},
            "hasTelemetry": telemetry,
            "streams": [{"streamName": "main", "profileToken": "Profile_2",
                         "path": "", "webRtc": True}],
        })
        print(f"  ..    added camera {camera_id} (ONVIF {sim_host}:{port})")


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

    rooms = children(vms, building["id"])

    # A room is either a plan of its own or a diagram standing on the floor.
    room = by_name(vms, rooms, ROOM_PLAN_NAME)
    if not check(room is not None and room.get("nodeKind") == "space",
                 f"the building holds {ROOM_PLAN_NAME} as a plan"):
        return None

    room_diagram = by_name(vms, rooms, ROOM_DIAGRAM_NAME)
    if not check(room_diagram is not None and room_diagram.get("nodeKind") == "diagram",
                 f"the building holds {ROOM_DIAGRAM_NAME} as a room diagram"):
        return None

    # The intermediate diagram: several racks on one schematic, each a canvas of its own.
    layout_racks = children(vms, room_diagram["id"])
    check(len(layout_racks) == 2 and {r.get("name") for r in layout_racks} == {"Rack B-01", "Rack B-02"},
          f"{ROOM_DIAGRAM_NAME} shows Rack B-01 and Rack B-02 on one diagram")

    racks = children(vms, room["id"])
    rack = by_name(vms, racks, "Rack A-01")
    if not check(rack is not None and rack.get("nodeKind") == "diagram",
                 "Room 101 holds Rack A-01 as a mnemoscheme"):
        return None

    computers = children(vms, rack["id"])
    computer = by_name(vms, computers, "Switch A-01")
    if not check(computer is not None and computer.get("nodeKind") == "diagram",
                 "Rack A-01 holds Switch A-01"):
        return None

    ports = children(vms, computer["id"])
    check(len(ports) == 8, f"Switch A-01 holds 8 ports (got {len(ports)})")
    port = by_name(vms, ports, "Port 3")
    check(port is not None, "Port 3 is among them")

    return {
        "map": demo_map, "building": building, "room": room, "room102": room_diagram,
        "layout_racks": layout_racks, "rack": rack,
        "switch": computer, "port": port, "ports": ports,
    }


def check_positions(vms, world):
    """Regions give the place; the geometry stays the diagram's own canvas (M2.2 step 1)."""
    ids = [world[key]["id"] for key in ("rack", "switch", "port", "room102")]
    ids += [rack["id"] for rack in world["layout_racks"]]
    diagrams = {d["id"]: d for d in diagrams_by_ids(vms, ids)}

    room = diagrams.get(world["room102"]["id"], {})
    check(room.get("geometry", {}).get("width") == 800 and room.get("geometry", {}).get("height") == 600,
          "the room diagram opens on its own 800x600 canvas")
    check(not room.get("regionId"), "the room diagram stands by its own rectangle on the floor")

    # The children come back unordered; the rack's own name says which slot it fills.
    slots = {"Rack B-01": "rack-1", "Rack B-02": "rack-2"}
    for rack in world["layout_racks"]:
        region = slots[rack["name"]]
        placed = diagrams.get(rack["id"], {})
        check(placed.get("regionId") == region,
              f"{rack['name']} fills the room diagram's {region} region")
        check(placed.get("geometry", {}).get("width") == 240,
              f"{rack['name']} keeps its own 240-wide canvas beside the region")

    rack = diagrams.get(world["rack"]["id"], {})
    check(rack.get("geometry", {}).get("width") == 240 and rack.get("geometry", {}).get("height") == 720,
          "the plan rack opens on its own 240x720 canvas")
    check(not rack.get("regionId"), "the plan rack stands by its own rectangle in the room")

    computer = diagrams.get(world["switch"]["id"], {})
    check(computer.get("regionId") == "slot-1", "the switch sits in the rack's slot-1 region")
    check(computer.get("geometry", {}).get("width") == 600,
          "the switch keeps its own 600-wide canvas beside the region")

    port = diagrams.get(world["port"]["id"], {})
    check(port.get("regionId") == "port-3", "Port 3 sits in the switch's port-3 region")


def check_types(vms):
    types = {t.get("name"): t for t in diagram_types(vms)}

    rack = types.get("Rack 42U", {})
    rack_regions = {r.get("regionKey") for r in rack.get("regions") or []}
    check({"slot-1", "temp", "humidity"} <= rack_regions,
          f"Rack 42U offers slots, temp and humidity (has {sorted(rack_regions)})")

    computer = types.get("Cisco Catalyst 9300", {})
    computer_regions = {r.get("regionKey") for r in computer.get("regions") or []}
    check(len(computer_regions) == 8 and "port-8" in computer_regions,
          "Cisco Catalyst 9300 offers its eight ports")

    check("RJ45 Port" in types, "the port type is in the dictionary")


def check_cameras(vms, world, allow_missing):
    configured = {camera.get("cameraId") for camera in vms.cameras()}
    missing = [camera for camera in DEMO_CAMERAS if camera not in configured]

    if missing and not allow_missing:
        check(False, f"the four demo cameras are configured (missing {', '.join(missing)})")
        return

    if missing:
        warn(f"missing cameras skipped: {', '.join(missing)}")
        return

    placed = {p.get("cameraId"): p for p in placements(vms)}
    # The master and its PTZ slave work as one pair, so both stand in Room 101.
    expected = {
        "sim-master": world["room"]["id"],
        "sim-ptz": world["room"]["id"],
        "sim-master2": world["building"]["id"],
        "sim-ptz2": world["map"]["id"],
    }

    for camera_id, space_id in expected.items():
        placement = placed.get(camera_id)
        check(placement is not None and placement.get("spaceId") == space_id,
              f"{camera_id} stands in its space")


def check_monitor(vms, allow_missing):
    wall = next((m for m in monitors(vms) if m.get("name") == "UMD DC"), None)
    if not check(wall is not None, "the 'UMD DC' video wall is in VmsCfg"):
        return

    check(wall.get("layout") == "2x2", "the wall is a 2x2 grid")
    cameras = wall.get("cameraIds") or []
    if allow_missing:
        warn(f"the wall shows {len(cameras)} camera(s)")
    else:
        check(len(cameras) == 4, f"the wall shows the four cameras (got {len(cameras)})")


def check_levels(vms):
    names = {level.get("zoomLevel") for level in levels(vms)}
    check({"Campus", "Building", "Room", "Rack"} <= names,
          "the Campus, Building, Room and Rack layers are in the dictionary")


def check_alarm(vms, world):
    """The port is DISCONNECTED and the alarm walks up every parent to the map."""
    rows = {row.get("id"): row for row in alarmed(vms)}

    port = rows.get(world["port"]["id"], {})
    check(port.get("alarm") is True, "Port 3 is alarmed")

    for name in ("switch", "rack", "room", "building", "map"):
        row = rows.get(world[name]["id"], {})
        check(row.get("alarm") is True or (row.get("childrenAlarms") or 0) > 0,
              f"the alarm reaches the {name}")


def check_feeder(vms, world):
    """The rack shows the demo's own temperature and humidity, rewritten every five seconds."""
    print("  ..    waiting for the value feeder")
    time.sleep(6)
    names = {v.get("name") for v in values_of(vms, world["rack"]["id"])}
    check({"temp", "humidity"} <= names, f"the rack shows temp and humidity (has {sorted(names)})")


def ids_of(vms, world):
    ids = {key: world[key]["id"] for key in ("map", "building", "room", "room102", "rack", "switch", "port")}
    ids["layout"] = [rack["id"] for rack in world["layout_racks"]]
    return ids


def check_reinstall(vms, world):
    before = ids_of(vms, world)
    report = install_demo(vms)
    again = walk_the_world(vms)

    check(again is not None, "the world is still there after a second install")
    if again is None:
        return world

    check(ids_of(vms, again) == before, "every id is the same after a second install")
    check((report.get("created") or 0) == 0, "a second install creates nothing")
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

    print("Walking the world")
    world = walk_the_world(vms)
    if world is None:
        print("\nThe demo world is not in the shape it promises.")
        return 1

    print("Regions and canvases")
    check_positions(vms, world)

    print("Types")
    check_types(vms)

    print("Cameras and the video wall")
    check_cameras(vms, world, args.allow_missing_cameras)
    check_monitor(vms, args.allow_missing_cameras)

    print("Layers, readings and the alarm")
    check_levels(vms)
    check_feeder(vms, world)
    check_alarm(vms, world)

    print("Installing again")
    world = check_reinstall(vms, world)

    if not args.no_remove_check:
        print("A user's node beside the world")
        check_user_node_survives(vms, world)
        # The removal above took the world away; put it back for whoever uses the installation next.
        install_demo(vms)

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
