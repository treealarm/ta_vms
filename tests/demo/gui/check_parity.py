#!/usr/bin/env python3
"""The parity world read back through the public API.

The database inventory proves coverage; this proves the world is visible and related through the
gateway the product itself uses. Read-only: GETs and the read POSTs only — nothing is installed,
updated or removed.

    check_parity.py --prefix parity
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "onvifsim"))
from vmsapi import Vms  # noqa: E402

FAILED: list[str] = []


def check(ok: bool, what: str, detail: str = "") -> bool:
    print(f"{'  ok  ' if ok else ' FAIL '} {what}{f' — {detail}' if detail else ''}")
    if not ok:
        FAILED.append(what)
    return ok


def children(vms: Vms, parent_id: str) -> list[dict]:
    page = vms.call("GET", f"/api/map/objects/parent?parentId={parent_id}&count=1000") or {}
    return page.get("children") or []


def diagram_types(vms: Vms) -> list[dict]:
    return (vms.call("POST", "/api/map/diagrams/types", {}) or {}).get("diagramTypes") or []


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vms", default="http://localhost:5134")
    parser.add_argument("--user", default="admin")
    parser.add_argument("--password", default="admin")
    parser.add_argument("--prefix", default="parity")
    args = parser.parse_args()

    vms = Vms(args.vms, args.user, args.password)
    vms.login()

    demo = vms.call("GET", "/api/map/admin/demo") or {}
    check(demo.get("installed") is False, "the demo is not installed", str(demo))

    spaces = vms.spaces()
    root = next((s for s in spaces if s["name"] == f"{args.prefix}-map"), None)
    plan = next((s for s in spaces if s["name"] == f"{args.prefix}-plan"), None)
    if not check(root is not None and plan is not None, "the map and the plan exist"):
        return finish()

    check(plan.get("parentId") == root["id"], "the plan is a child of the map")
    check(plan.get("plan") is not None, "the plan has a picture")
    check(len(plan.get("footprint") or []) >= 3, "the plan has a footprint",
          f"{len(plan.get('footprint') or [])} points")

    plan_kids = children(vms, plan["id"])
    map_kids = children(vms, root["id"])
    check(any(k.get("nodeKind") == "diagram" for k in plan_kids), "a diagram stands on the plan")
    check(any(k.get("nodeKind") == "camera" for k in plan_kids), "a camera picture stands on the plan")
    check(any(k.get("nodeKind") == "camera" for k in map_kids), "a camera picture stands on the map")
    check(any(k.get("nodeKind") == "part" and k.get("ownerId") for k in map_kids),
          "a part replica stands on the map")

    placement_space = {}
    for placement in vms.call("GET", "/api/spatial/placements") or []:
        placement_space.setdefault(placement["spaceId"], []).append(placement)
    check(any(p.get("isReference") for p in placement_space.get(plan["id"], [])),
          "a reference camera placement is on the plan")
    check(any(p.get("isReference") for p in placement_space.get(root["id"], [])),
          "a reference camera placement is on the map")
    check(all(p.get("hfovDeg", 0) > 0 and p.get("rangeM", 0) > 0
              for p in placement_space.get(plan["id"], [])),
          "the placement carries a view angle and a range")

    check(len(vms.landmarks(plan["id"])) > 0, "the plan has landmarks")
    check(len(vms.landmarks(root["id"])) > 0, "the map has landmarks")

    ground = 0
    ptz = 0
    for placement in placement_space.get(plan["id"], []):
        camera_id = placement["cameraId"]
        ground += len((vms.get_ground_geo(camera_id) or {}).get("observations") or [])
    for placement in placement_space.get(root["id"], []):
        camera_id = placement["cameraId"]
        ptz += len((vms.get_ptz_geo(camera_id) or {}).get("observations") or [])
    check(ground > 0, "the fixed camera has ground observations", f"{ground}")
    check(ptz > 0, "the PTZ camera has landmark observations", f"{ptz}")

    types = diagram_types(vms)
    check(any(t.get("regions") for t in types), "a diagram type has regions")
    check(any(region.get("part")
              for t in types for region in (t.get("regions") or [])),
          "a diagram type binds a device part to a region")
    check(any(t.get("sourceKind") == "camera" and t.get("autoAttach") for t in types),
          "a device template follows the camera's model")

    monitor = next((m for m in vms.call("GET", "/api/monitor") or []
                    if m.get("kind") == "wall" and m.get("slots")), None)
    check(monitor is not None, "a video wall has slots",
          monitor.get("name") if monitor else "")

    subs = (vms.call("GET", "/api/map/admin/event-subscriptions") or {}).get("subscriptions") or []
    sensor_rule = next((s for s in subs
                        if s.get("eventName") == "sensor"
                        and {a.get("kind") for a in s.get("actions") or []}
                        >= {"retain_recording", "set_relay"}), None)
    check(sensor_rule is not None, "the sensor rule has both actions",
          sensor_rule.get("name") if sensor_rule else "")

    rights = (vms.call("GET", f"/api/map/rights?objectId={root['id']},{plan['id']}")
              or {}).get("rights") or []
    check(any(r.get("objectId") == root["id"] and r.get("action") == "view" for r in rights),
          "view is granted on the map")
    check(any(r.get("objectId") == plan["id"] and r.get("action") == "control" for r in rights),
          "control is granted on the plan")

    levels = (vms.call("GET", "/api/map/levels") or {}).get("levels") or []
    check(any(level.get("zoomLevel") == f"{args.prefix}-ground" for level in levels),
          "the custom scale layer exists")

    finish()


def finish() -> None:
    if FAILED:
        print(f"\n{len(FAILED)} check(s) failed")
        sys.exit(1)
    print("\nall checks passed")


if __name__ == "__main__":
    main()
