#!/usr/bin/env python3
"""The parity world's behaviour on the simulator, not just its rows.

Configuration can be present and still do nothing. These checks act on the running stand: the
master's relay closes the slave's input, the sensor rule raises an alarm and drives the lamp, the
PTZ slave follows the walking person, and the vehicle camera moves with its own telemetry.

    functional_smoke.py --prefix parity
"""

from __future__ import annotations

import argparse
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "onvifsim"))
from labhttp import request  # noqa: E402
from vmsapi import Vms  # noqa: E402

FAILED: list[str] = []


def check(ok: bool, what: str, detail: str = "") -> bool:
    print(f"{'  ok  ' if ok else ' FAIL '} {what}{f' — {detail}' if detail else ''}")
    if not ok:
        FAILED.append(what)
    return ok


def wait_for(what: str, probe, timeout: float, step: float = 2.0):
    deadline = time.monotonic() + timeout
    last = None
    while time.monotonic() < deadline:
        last = probe()
        if last:
            return last
        time.sleep(step)
    raise AssertionError(f"{what} did not happen within {timeout:.0f} s")


def camera_by_name(vms: Vms, name: str) -> dict | None:
    return next((c for c in vms.cameras() if c.get("name") == name), None)


def part_live(vms: Vms, camera_id: str, kind: str, token: str):
    path = ("digital-inputs" if kind == "input" else "relay-outputs")
    rows = vms.call("GET", f"/api/DeviceIo/cameras/{camera_id}/{path}") or []
    return next((row for row in rows if row.get("token") == token), None)


def active_alarms(vms: Vms) -> int:
    data = vms.call("POST", "/api/map/alarms", {"pageSize": 100}) or {}
    return len([a for a in (data.get("open") or {}).get("alarms") or []
                if a.get("status") == "active"])


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vms", default="http://localhost:5134")
    parser.add_argument("--sim", default="http://127.0.0.1:8090")
    parser.add_argument("--user", default="admin")
    parser.add_argument("--password", default="admin")
    parser.add_argument("--prefix", default="parity")
    args = parser.parse_args()

    vms = Vms(args.vms, args.user, args.password)
    vms.login()

    master = camera_by_name(vms, f"{args.prefix}-master")
    ptz = camera_by_name(vms, f"{args.prefix}-ptz")
    vehicle = camera_by_name(vms, f"{args.prefix}-vehicle")
    if not check(bool(master and ptz and vehicle), "the three parity cameras are provisioned"):
        finish()

    # ── 1. Contact, alarm and the routing rule ────────────────────────────────
    try:
        # The simulator keeps its relays between runs, and the master's peer is wired to the
        # slave's input: a contact left closed would raise its own alarm before the test does.
        vms.call("POST", f"/api/DeviceIo/cameras/{master['cameraId']}/relay-outputs/peer/state",
                 {"active": False})
        wait_for(
            "the contact to be open before the test",
            lambda: (part_live(vms, ptz["cameraId"], "input", "input") or {}).get("liveActive") is False,
            60)

        vms.call("POST", f"/api/DeviceIo/cameras/{master['cameraId']}/relay-outputs/peer/state",
                 {"active": True})
        input_row = wait_for(
            "the slave's input closing",
            lambda: (lambda row: row if row and row.get("liveActive") is True else None)(
                part_live(vms, ptz["cameraId"], "input", "input")),
            30)
        check(True, "the master's relay closes the slave's input", str(input_row.get("liveActive")))

        wait_for("the alarm to rise", lambda: active_alarms(vms) > 0, 30)
        check(True, "the armed input raises an alarm")

        lamp = wait_for(
            "the rule's set_relay action",
            lambda: (lambda row: row if row and row.get("liveActive") is True else None)(
                part_live(vms, master["cameraId"], "relay", "lamp")),
            30)
        check(True, "the routing rule drives the lamp", str(lamp.get("liveActive")))

        vms.call("POST", f"/api/DeviceIo/cameras/{master['cameraId']}/relay-outputs/peer/state",
                 {"active": False})
        # The contact clears the moment the relay opens, but the alarm follows the map's own state
        # read, which is slower: the device reports its parts on a poll, so the return can take
        # most of a minute. Bounded and named, not a silent sleep.
        wait_for("the alarm to close", lambda: active_alarms(vms) == 0, 90)
        check(True, "opening the contact closes the alarm")
    except AssertionError as e:
        check(False, "the contact → alarm → rule path", str(e))

    # ── 2. The PTZ slave follows the walking person ───────────────────────────
    try:
        first = request("GET", f"{args.sim}/api/cameras/ptz/pose")
        moved = wait_for(
            "the slave to move",
            lambda: (lambda pose: pose if (abs(pose["pan"] - first["pan"]) > 0.2
                                           or abs(pose["tilt"] - first["tilt"]) > 0.2) else None)(
                request("GET", f"{args.sim}/api/cameras/ptz/pose")),
            90, step=5)
        check(True, "the PTZ slave follows the master's target",
              f"pan {first['pan']:.2f} → {moved['pan']:.2f}")
    except AssertionError as e:
        check(False, "the PTZ slave follows the master's target", str(e))

    # ── 3. geo_from_device moves the camera's placement ──────────────────────
    try:
        def vehicle_at():
            placements = vms.call("GET", "/api/spatial/placements") or []
            return next((p for p in placements if p.get("cameraId") == vehicle["cameraId"]), None)

        first = vehicle_at()
        moved = wait_for(
            "the vehicle's placement to move",
            lambda: (lambda p: p if p and (abs(p["lat"] - first["lat"]) > 1e-7
                                           or abs(p["lon"] - first["lon"]) > 1e-7) else None)(
                vehicle_at()),
            90, step=5)
        check(True, "geo_from_device moves the camera's placement",
              f"{first['lat']:.6f},{first['lon']:.6f} → {moved['lat']:.6f},{moved['lon']:.6f}")
    except AssertionError as e:
        check(False, "geo_from_device moves the camera's placement", str(e))

    finish()


def finish() -> None:
    if FAILED:
        print(f"\n{len(FAILED)} check(s) failed")
        sys.exit(1)
    print("\nall checks passed")


if __name__ == "__main__":
    main()
