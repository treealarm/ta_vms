#!/usr/bin/env python3
"""A virtual bench for PTZ aiming: OnvifSim's small world, driven through ta_vms.

Steps, each one checking what it did and exiting 1 with the reason when something is off:

    up          build and start OnvifSim from ../OnvifSim in Docker, on ports clear of the dev
                stack, and load the world
    provision   add both cameras to ta_vms over ONVIF, a map, the landmarks and where the cameras stand
    calibrate   PTZ by landmarks (head driven in the sim, position read back over ONVIF through
                VmsOnvif) and the master's ground marks (a person standing on each landmark, the foot
                of the box ta_vms received from the camera's metadata track)
    aim-check   follow the walking person with the existing "aim at a point of the map" call and
                measure where the PTZ really looks, against the sim's ground truth
    bind        bind the PTZ to the master through the map (method "geo") and switch tracking on
    run         let ta_vms follow the person on its own, from the master's detections, and measure
    down        stop OnvifSim's container (--purge also removes the cameras and the map from ta_vms)
    all         up, provision, calibrate, aim-check, bind, run, down

The ta_vms dev stack must already be running (scripts/dev-up.sh or F5). See README.md.
"""

import argparse
import json
import math
import os
import statistics
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import world as W  # noqa: E402
from labhttp import HttpError  # noqa: E402
from simapi import Sim  # noqa: E402
from vmsapi import Vms  # noqa: E402

REPO = Path(__file__).resolve().parents[2]
COMPOSE_FILE = Path(__file__).resolve().parent / "docker-compose.yml"
STATE_DIR = Path("/tmp/onvifsim-lab")
STATE_FILE = STATE_DIR / "state.json"
SPACE_NAME = "OnvifSim lab"
PROFILE = "Profile_2"  # OnvifSim's H.264 profile, the one media_server pulls
MARK_WALKER = "lab-mark"


class Failed(Exception):
    pass


def require(condition, message):
    if not condition:
        raise Failed(message)


def log(message):
    print(message, flush=True)


def read_env():
    values = {}
    env = REPO / ".env"
    if env.exists():
        for line in env.read_text().splitlines():
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                key, _, value = line.partition("=")
                values[key.strip()] = value.strip().strip('"')
    return values


def load_state():
    return json.loads(STATE_FILE.read_text()) if STATE_FILE.exists() else {}


def save_state(state):
    STATE_DIR.mkdir(parents=True, exist_ok=True)
    STATE_FILE.write_text(json.dumps(state, indent=2))


class Lab:
    def __init__(self, args):
        self.args = args
        self.sim = Sim(f"http://127.0.0.1:{args.sim_port}")
        self.vms = Vms(args.vms, args.user, args.password)
        self.state = load_state()

    def cam_id(self, camera):
        return f"{self.args.prefix}{camera.id}"

    @property
    def onvif_base(self):
        return self.args.onvif_base_port

    # ── up / down ───────────────────────────────────────────────────────────────

    def up(self):
        if self.sim.ready():
            log("OnvifSim already running")
        else:
            self._start_sim()
        self.sim.put_world(W.build_world(self.onvif_base))
        status = self.sim.status()
        require(status.get("h264"), "OnvifSim offers no H.264 (ffmpeg not found): media_server needs Profile_2")
        ports = status.get("onvifPorts", {})
        expected = {W.MASTER.id: self.onvif_base, W.SLAVE.id: self.onvif_base + 1}
        require(ports == expected, f"OnvifSim ONVIF ports are {ports}, expected {expected}")
        log(f"OnvifSim world loaded: ONVIF {expected}, RTSP {status.get('rtspPort')}")

    def _compose(self, *command, check=True):
        a = self.args
        env = dict(os.environ,
                   ONVIFSIM_DIR=str(Path(a.sim_dir).resolve()), ONVIFSIM_PORT=str(a.sim_port),
                   ONVIFSIM_RTSP_PORT=str(a.rtsp_port), ONVIFSIM_RTP_PORT=str(a.rtp_port),
                   ONVIFSIM_RTP_PORT_END=str(a.rtp_port + 1),
                   ONVIFSIM_ONVIF_BASE_PORT=str(a.onvif_base_port),
                   ONVIFSIM_ONVIF_LAST_PORT=str(a.onvif_base_port + 1))
        result = subprocess.run(["docker", "compose", "-f", str(COMPOSE_FILE), *command], env=env)
        require(not check or result.returncode == 0, f"docker compose {' '.join(command)} failed")

    def _start_sim(self):
        sim_dir = Path(self.args.sim_dir).resolve()
        require((sim_dir / "Dockerfile").exists(), f"OnvifSim is not at {sim_dir} (--sim-dir)")
        # --build every time: the layers are cached, so it costs nothing unless ../OnvifSim changed,
        # and the bench never runs an image older than the checkout next to it.
        log("building and starting OnvifSim in Docker (the first build takes a few minutes)")
        self._compose("up", "-d", "--build")
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            if self.sim.ready():
                log("OnvifSim is up")
                return
            time.sleep(1)
        raise Failed(f"OnvifSim did not answer within a minute; see docker compose -f {COMPOSE_FILE} logs")

    def down(self):
        # -v: the world lives in the container's anonymous /data volume; up loads it afresh anyway.
        self._compose("down", "-v")
        log("OnvifSim stopped")
        if self.args.purge:
            self.vms.login()
            for camera in (W.MASTER, W.SLAVE):
                try:
                    self.vms.delete_camera(self.cam_id(camera))
                    log(f"removed camera {self.cam_id(camera)}")
                except HttpError as e:
                    log(f"camera {self.cam_id(camera)}: {e}")
            space_id = self.state.pop("space_id", None)
            if space_id:
                self.vms.delete_space(space_id)
                log(f"removed space {space_id}")
            self.state.pop("landmarks", None)
        save_state(self.state)

    def status(self):
        self._compose("ps", check=False)
        log(json.dumps(self.state, indent=2))

    # ── provision ───────────────────────────────────────────────────────────────

    def provision(self):
        self.vms.login()
        existing = {c["cameraId"] for c in self.vms.cameras()}
        for i, camera in enumerate((W.MASTER, W.SLAVE)):
            cam_id = self.cam_id(camera)
            if cam_id in existing and self.args.reset:
                self.vms.delete_camera(cam_id)
                existing.discard(cam_id)
            if cam_id in existing:
                log(f"camera {cam_id} already in ta_vms")
                continue
            port = self.onvif_base + i
            self.vms.add_camera({
                "cameraId": cam_id,
                "name": f"OnvifSim {camera.name}",
                "serverId": self.args.server_id,
                "onvifConn": {"ip": "127.0.0.1", "port": port, "xaddr": f"http://127.0.0.1:{port}/onvif/device_service"},
                "cred": {"user": "admin", "pass": "admin"},
                "hasTelemetry": camera.kind == "ptz",
                "streams": [{"streamName": "main", "profileToken": PROFILE, "path": "", "webRtc": True}],
            })
            log(f"added camera {cam_id} (ONVIF 127.0.0.1:{port}, {PROFILE})")

        space_id = self._ensure_space()
        landmarks = self._ensure_landmarks(space_id)
        for camera in (W.MASTER, W.SLAVE):
            lat, lon = W.to_wgs84(camera.x, camera.z, self.args.lat0, self.args.lon0)
            self.vms.upsert_placement(self.cam_id(camera), space_id, {
                "cameraId": self.cam_id(camera), "spaceId": space_id, "isReference": True,
                "lat": lat, "lon": lon, "heightM": camera.y, "headingDeg": camera.heading,
                "hfovDeg": camera.hfov if camera.kind == "fixed" else W.SLAVE_LIMITS.fov_wide,
                "rangeM": 60,
            })
        log(f"space '{SPACE_NAME}' ({space_id}): {len(landmarks)} landmarks, both cameras placed")
        self.state.update(space_id=space_id, landmarks=landmarks)
        save_state(self.state)

        log("waiting for the master's person boxes to reach ta_vms (RTSP metadata -> VmsOnvif -> analytics)")
        boxes = self._master_boxes(seconds=60, wanted=1)
        require(boxes, "no ONVIF person box from the master reached /api/analytics/live within 60 s: "
                       "check media_server's log for 'ONVIF metadata track found' and VmsOnvif's for 'scene objects'")
        log("the master's detections arrive in ta_vms")

    def _ensure_space(self):
        lat0, lon0 = self.args.lat0, self.args.lon0
        for space in self.vms.spaces():
            if space.get("name") == SPACE_NAME and not space.get("parentId"):
                return space["id"]
        sw = W.to_wgs84(-100, 100, lat0, lon0)
        ne = W.to_wgs84(100, -100, lat0, lon0)
        space = self.vms.upsert_space({
            "name": SPACE_NAME, "kind": "map", "realCoordinates": True, "ordinal": 0, "elevationM": 0,
            "view": {"southWest": {"lat": sw[0], "lon": sw[1]}, "northEast": {"lat": ne[0], "lon": ne[1]}},
            "footprint": [],
        })
        return space["id"]

    def _ensure_landmarks(self, space_id):
        # Matched on the first word ("L1"), so a landmark renamed here keeps its id -- and with it
        # every observation and mark already recorded against it.
        by_name = {lm["name"].split()[0]: lm["id"] for lm in self.vms.landmarks(space_id) if lm["name"]}
        ids = {}
        for lm in W.LANDMARKS:
            lat, lon = W.to_wgs84(lm.x, lm.z, self.args.lat0, self.args.lon0)
            body = {"spaceId": space_id, "name": lm.label, "lat": lat, "lon": lon, "heightM": 0}
            if lm.name in by_name:
                body["id"] = by_name[lm.name]
            ids[lm.name] = self.vms.upsert_landmark(body)["id"]
        return ids

    def _master_boxes(self, seconds, wanted):
        """Person boxes of the master, as ta_vms streams them, until `wanted` have arrived."""
        master = self.cam_id(W.MASTER)
        found = []
        for d in self.vms.live_detections(seconds):
            if d.get("cameraId") == master and d.get("source") == "onvif" and d.get("kind") == "person":
                found.append(d)
                if len(found) >= wanted:
                    break
        return found

    # ── calibrate ───────────────────────────────────────────────────────────────

    def calibrate(self):
        self.vms.login()
        require(self.state.get("landmarks"), "run provision first")
        slave = self.cam_id(W.SLAVE)
        # A slave already following its master would turn towards the person standing on each
        # landmark while it is being calibrated. Held, as the calibration wizard holds it.
        self.vms.hold(slave, 300)
        self.sim.delete_walker(W.WALKER_ID)
        try:
            self._calibrate_ptz()
            self._calibrate_ground()
        finally:
            self.sim.delete_walker(MARK_WALKER)
            self.sim.put_walker(W.WALKER_ID, W.walker_json(W.WALKER_ROUTE))
            try:
                self.vms.hold(slave, 0)
            except HttpError as e:
                # Best effort: the hold lapses by itself, and failing here would hide why the
                # calibration stopped.
                log(f"could not release the hold on {slave}: {e}")

    def _calibrate_ptz(self):
        slave = self.cam_id(W.SLAVE)
        ids = self.state["landmarks"]
        observations = []
        log(f"PTZ calibration of {slave}: centre each landmark, read the position back over ONVIF")
        log("  lm   pan°     tilt°    | ONVIF pan  tilt    | expected pan tilt")
        for lm in W.LANDMARKS:
            pan, tilt = W.ptz_degrees(W.SLAVE, (lm.x, 0.0, lm.z))
            self.sim.goto(W.SLAVE.id, pan, tilt, 0.0)
            self.sim.wait_idle(W.SLAVE.id)
            st = self.vms.ptz_status(slave, PROFILE)
            require(st.get("available"), f"GET /api/ptz/status for {slave}: {st}")
            require(not st.get("moving"), f"{slave} still reports MOVING after the sim head stopped")
            gx, gy = W.to_generic(pan, tilt)
            log(f"  {lm.name}  {pan:7.2f}  {tilt:7.2f}  | {st['pan']:8.4f} {st['tilt']:7.4f} | {gx:8.4f} {gy:7.4f}")
            require(abs(st["pan"] - gx) < 1e-3 and abs(st["tilt"] - gy) < 1e-3,
                    f"ONVIF reports ({st['pan']:.4f}, {st['tilt']:.4f}) at {lm.name}, expected ({gx:.4f}, {gy:.4f})")
            observations.append({"landmarkId": ids[lm.name], "pan": st["pan"], "tilt": st["tilt"]})

        result = self.vms.put_ptz_geo(slave, observations)
        require(result and result.get("fitted"), f"the PTZ geo fit failed: {result and result.get('error')}")
        worst = max(max(abs(o.get("panResidual") or 0), abs(o.get("tiltResidual") or 0)) for o in result["observations"])
        log(f"  fitted; worst leave-one-out residual {worst:.5f} (generic units)")
        require(worst < 0.01, f"leave-one-out residual {worst:.4f} is too large for an exactly linear head")

    def _calibrate_ground(self):
        master = self.cam_id(W.MASTER)
        ids = self.state["landmarks"]
        observations = []
        log(f"ground marks of {master}: a person stands on each landmark, the foot of the box ta_vms received")
        log("  lm   x       y       | expected x  y       | samples")
        for lm in W.LANDMARKS:
            self.sim.put_walker(MARK_WALKER, W.walker_json([(lm.x, lm.z)]))
            time.sleep(1.5)  # let boxes from the previous spot drain through the pipeline
            boxes = self._master_boxes(seconds=6, wanted=8)[-5:]
            require(len(boxes) >= 3, f"too few boxes for the person on {lm.name}: {len(boxes)}")
            xs = [b["bboxX"] + b["bboxWidth"] / 2 for b in boxes]
            ys = [b["bboxY"] + b["bboxHeight"] for b in boxes]
            x, y = statistics.median(xs), statistics.median(ys)
            ex, ey = W.project(W.MASTER, (lm.x, 0.0, lm.z))
            log(f"  {lm.name}  {x:.4f}  {y:.4f}  | {ex:.4f}  {ey:.4f}  | {len(boxes)}")
            # Not data, a diagnosis: a mark this far off means the boxes are not where the picture is.
            require(abs(x - ex) < 0.03 and abs(y - ey) < 0.03,
                    f"the box foot on {lm.name} is at ({x:.3f}, {y:.3f}), the picture puts it at ({ex:.3f}, {ey:.3f})")
            observations.append({"landmarkId": ids[lm.name], "x": x, "y": y})

        result = self.vms.put_ground_geo(master, observations)
        require(result and result.get("fitted"), f"the ground fit failed: {result and result.get('error')}")
        residuals = [o.get("residualM") for o in result["observations"] if o.get("residualM") is not None]
        worst = max(residuals) if residuals else 0.0
        log(f"  fitted; worst residual {worst:.3f} m")
        require(worst < 0.3, f"ground residual {worst:.2f} m is too large on a flat world without distortion")

    # ── aim-check ───────────────────────────────────────────────────────────────

    def aim_check(self):
        self.vms.login()
        space_id = self.state.get("space_id")
        require(space_id, "run provision first")
        slave = self.cam_id(W.SLAVE)
        self.sim.put_walker(W.WALKER_ID, W.walker_json(W.WALKER_ROUTE))
        log(f"aim {slave} at the walking person through POST /api/ptz-geo/{{id}}/aim, {self.args.seconds:.0f} s")
        log("   t    person x  z     | ta_vms az°  el°    | true az°  el°    | aim err°  lag err°  in centre")
        aim_errors, lag_errors, centred = [], [], []
        start = time.monotonic()
        while time.monotonic() - start < self.args.seconds:
            target = self.sim.walker_pose(W.WALKER_ID)
            feet = (target["x"], target["y"], target["z"])
            lat, lon = W.to_wgs84(target["x"], target["z"], self.args.lat0, self.args.lon0)
            aim = self.vms.aim_world(slave, space_id, lat, lon, 0.0)
            pose = self.sim.wait_idle(W.SLAVE.id)
            cam = (pose["position"]["x"], pose["position"]["y"], pose["position"]["z"])
            axis = W.direction(pose["heading"], pose["pitch"])
            to_target = tuple(f - c for f, c in zip(feet, cam))
            true_az, true_el, _ = W.bearing(cam, feet)
            aim_err = W.angle_between(axis, to_target)

            now = self.sim.walker_pose(W.WALKER_ID)
            now_feet = (now["x"], now["y"], now["z"])
            lag_err = W.angle_between(axis, tuple(f - c for f, c in zip(now_feet, cam)))
            in_centre = W.in_middle_third(pose, (now["x"], now["y"] + W.WALKER_HEIGHT / 2, now["z"]))

            aim_errors.append(aim_err)
            lag_errors.append(lag_err)
            centred.append(in_centre)
            log(f"{time.monotonic() - start:5.1f}  {target['x']:7.2f} {target['z']:7.2f} | "
                f"{aim['azimuthDeg']:8.2f} {aim['elevationDeg']:7.2f} | {true_az:8.2f} {true_el:7.2f} | "
                f"{aim_err:7.3f}  {lag_err:7.3f}   {'yes' if in_centre else 'NO'}")
            time.sleep(max(0.0, self.args.interval - 1.0))

        require(aim_errors, "no aim was made")
        report = _stats("aim error (where it was sent)", aim_errors) + "\n" + _stats("lag error (person has moved on)", lag_errors)
        log(report)
        if centred:
            log(f"person within the central third of the slave's picture: {sum(centred)}/{len(centred)}")
        mean = statistics.mean(aim_errors)
        p95 = _percentile(aim_errors, 95)
        require(mean < 1.0 and p95 < 1.5, f"aim error mean {mean:.2f}°, p95 {p95:.2f}°: expected under 1° / 1.5°")
        require(not centred or sum(centred) / len(centred) > 0.9, "the person is not in the centre of the slave's picture")
        log("aim-check passed")

    # ── bind / run ──────────────────────────────────────────────────────────────

    def bind(self):
        self.vms.login()
        master, slave = self.cam_id(W.MASTER), self.cam_id(W.SLAVE)
        self.vms.put_binding(slave, {"masterCameraId": master, "method": "geo", "points": []})
        binding = self.vms.get_binding(slave)
        require(binding and binding.get("method") == "geo", f"the binding did not come back as geo: {binding}")
        require(not binding.get("unusable"), f"the geo binding cannot aim: {binding.get('unusable')}")
        self.vms.put_policy(slave, {"enabled": True, "kinds": ["person"], "sources": ["onvif"]})
        log(f"{slave} follows {master} through the map; waiting for its session")

        deadline = time.monotonic() + 30
        state = None
        while time.monotonic() < deadline:
            state = next((s for s in self.vms.tracking_states() if s["cameraId"] == slave), None)
            if state and state.get("active") and not state.get("lastError"):
                log(f"session running: following master {state.get('masterCameraId')}")
                return
            time.sleep(1)
        raise Failed(f"no running session for {slave} after 30 s: {state}. "
                     "Is vmsptztrackingsingleton subscribed? scripts/check-subscriptions.py")

    def run(self):
        self.vms.login()
        slave = self.cam_id(W.SLAVE)
        self.sim.put_walker(W.WALKER_ID, W.walker_json(W.WALKER_ROUTE))
        seconds = self.args.seconds
        log(f"ta_vms follows the person with {slave} on its own for {seconds:.0f} s")

        log("   t     person x  z     | moving | error°   | event")
        tracking, arrivals, centred = [], [], []
        # A move is seen by the head's position changing, not only by its MOVING flag: a short slew
        # finishes between two samples.
        rest, started_at, prev_feet = None, None, None
        start = time.monotonic()
        while time.monotonic() - start < seconds:
            pose = self.sim.camera_pose(W.SLAVE.id)
            person = self.sim.walker_pose(W.WALKER_ID)
            cam = (pose["position"]["x"], pose["position"]["y"], pose["position"]["z"])
            axis = W.direction(pose["heading"], pose["pitch"])
            feet = (person["x"], person["y"], person["z"])
            err = W.angle_between(axis, tuple(f - c for f, c in zip(feet, cam)))
            # The person's middle in the slave's picture, from the true pose: the slave has no
            # analytics of its own in this world, by design.
            centred.append(W.in_middle_third(pose, (feet[0], feet[1] + W.WALKER_HEIGHT / 2, feet[2])))
            moving = bool(pose.get("moving"))
            head = (pose["pan"], pose["tilt"])
            event = ""
            if moving and started_at is None:
                started_at = prev_feet or feet
                event = "command"
            elif not moving:
                if rest is not None and max(abs(a - b) for a, b in zip(head, rest)) > 1e-3:
                    # Against where the person was when the head set off (to within a sample), which
                    # is close to what the command was computed from: the precision of the chain,
                    # not of the follow.
                    sent_for = started_at or prev_feet or feet
                    arrival = W.angle_between(axis, tuple(f - c for f, c in zip(sent_for, cam)))
                    arrivals.append(arrival)
                    event = f"{'' if started_at else 'command, '}arrived {arrival:.2f}° from where it was sent"
                rest, started_at = head, None
                tracking.append(err)
            log(f"{time.monotonic() - start:6.2f}  {person['x']:7.2f} {person['z']:7.2f} | "
                f"{'yes' if moving else ' - ':>6} | {err:7.3f}  | {event}")
            prev_feet = feet
            time.sleep(0.25)

        state = next((s for s in self.vms.tracking_states() if s["cameraId"] == slave), {})
        require(arrivals, f"the slave never moved; tracking state: {state}")
        log(_stats("arrival error (vs where the person was when the head set off)", arrivals))
        log(_stats("tracking error (head at rest vs the person now)", tracking))
        log(f"person in the middle third of the slave's picture: {sum(centred)}/{len(centred)} samples")
        log(f"commands: {len(arrivals)}; tracking state: {json.dumps(state)}")

        mean, p95 = statistics.mean(arrivals), _percentile(arrivals, 95)
        require(mean < 1.5 and p95 < 2.5, f"arrival error mean {mean:.2f}°, p95 {p95:.2f}°")
        require(_percentile(tracking, 95) < 6, f"tracking error p95 {_percentile(tracking, 95):.2f}°")
        require(sum(centred) / len(centred) > 0.9,
                "the person is out of the slave's central third too often")
        log("run passed")


def _percentile(values, p):
    ordered = sorted(values)
    k = (len(ordered) - 1) * p / 100
    lo, hi = math.floor(k), math.ceil(k)
    return ordered[lo] + (ordered[hi] - ordered[lo]) * (k - lo)


def _stats(label, values):
    return (f"{label}: n={len(values)} mean {statistics.mean(values):.3f}° "
            f"p95 {_percentile(values, 95):.3f}° max {max(values):.3f}°")


def main():
    env = read_env()
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("step", choices=["up", "provision", "calibrate", "aim-check", "bind", "run", "down", "status", "all"])
    p.add_argument("--vms", default=f"http://localhost:{env.get('WEB_VMS_PORT', '5134')}")
    p.add_argument("--sim-dir", default=str(REPO.parent / "OnvifSim"))
    p.add_argument("--sim-port", type=int, default=8090)
    p.add_argument("--rtsp-port", type=int, default=8560)
    p.add_argument("--rtp-port", type=int, default=6980)
    p.add_argument("--onvif-base-port", type=int, default=8101)
    p.add_argument("--server-id", default="vms")
    p.add_argument("--user", default="admin")
    p.add_argument("--password", default="admin")
    p.add_argument("--prefix", default="sim-", help="ta_vms camera ids are <prefix>master, <prefix>ptz")
    p.add_argument("--lat0", type=float, default=W.DEFAULT_LAT0)
    p.add_argument("--lon0", type=float, default=W.DEFAULT_LON0)
    p.add_argument("--seconds", type=float, default=60, help="aim-check and run duration")
    p.add_argument("--interval", type=float, default=3, help="aim-check: seconds between aims")
    p.add_argument("--reset", action="store_true", help="provision: re-add cameras that already exist")
    p.add_argument("--purge", action="store_true", help="down: also remove the cameras and the map from ta_vms")
    args = p.parse_args()

    W.check()
    lab = Lab(args)
    steps = {
        "up": [lab.up], "provision": [lab.provision], "calibrate": [lab.calibrate],
        "aim-check": [lab.aim_check], "bind": [lab.bind], "run": [lab.run],
        "down": [lab.down], "status": [lab.status],
        "all": [lab.up, lab.provision, lab.calibrate, lab.aim_check, lab.bind, lab.run],
    }[args.step]
    try:
        for step in steps:
            step()
    except (Failed, HttpError) as e:
        log(f"FAILED: {e}")
        return 1
    finally:
        if args.step == "all":
            lab.down()
    return 0


if __name__ == "__main__":
    sys.exit(main())
