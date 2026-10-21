#!/usr/bin/env python3
"""A virtual bench for PTZ aiming: OnvifSim's small world, driven through ta_vms.

Two master/slave pairs watch the same yard: one stands on a map in real coordinates, the other on
a plan in made-up ones, whose picture is the yard photographed from above. --pair picks one; by
default every step does both, one after the other.

Steps, each one checking what it did and exiting 1 with the reason when something is off:

    up          build and start OnvifSim from ../OnvifSim in Docker, on ports clear of the dev
                stack, and load the world
    provision   add the cameras to ta_vms over ONVIF; the map, and the plan with its picture; the
                landmarks in each, and where each pair's cameras stand
    calibrate   PTZ by landmarks (head driven in the sim, position read back over ONVIF through
                VmsOnvif) and the master's ground marks (a person standing on each landmark, the foot
                of the box ta_vms received from the camera's metadata track)
    aim-check   follow the walking person with the existing "aim at a point of the map" call and
                measure where the PTZ really looks, against the sim's ground truth
    bind        bind the PTZ to its master through the map (method "geo") and switch tracking on;
                for the plan's pair, first check that a master on the map is refused
    run         let ta_vms follow the person on its own, from the master's detections, and measure
    edge        edge import, end to end: the map pair's master drops off the network for --outage
                seconds and goes on recording on its own card; ta_vms has to find the hole in its
                archive, find the footage on the camera over Profile G, replay it and file it
    down        stop OnvifSim's container (--purge also removes the cameras and the spaces from ta_vms)
    all         up, provision, calibrate, aim-check, bind, run, down
    matrix      the map pair hung every way in world.MOUNTS (or --mounts a,b): for each, rehang the
                cameras in the sim, calibrate, aim-check, bind and run, then print one table; the
                bench is left level and calibrated again

--mount <name> hangs the map pair one of those ways for the ordinary steps (up loads it). A mount
expected to "measure" turns the quality thresholds into notes, so its steps run to the end and
report what the fits make of it.

The ta_vms dev stack must already be running (scripts/dev-up.sh or F5). See README.md.
"""

import argparse
import datetime
import json
import math
import os
import statistics
import subprocess
import sys
import time
import urllib.parse
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import world as W  # noqa: E402
from labhttp import HttpError  # noqa: E402
from simapi import Sim  # noqa: E402
from vmsapi import Vms  # noqa: E402

REPO = Path(__file__).resolve().parents[2]
COMPOSE_FILE = Path(__file__).resolve().parent / "docker-compose.yml"
STATE_DIR = Path("/tmp/onvifsim-lab")
SPACE_NAMES = {W.MAP_PAIR.name: "OnvifSim lab", W.PLAN_PAIR.name: "OnvifSim lab plan"}
PROFILE = "Profile_2"  # OnvifSim's H.264 profile, the one video_srv pulls
CONTINUOUS = "00000000-0000-0000-0000-000000000001"  # ta_vms's built-in continuous recording profile
# How long ta_vms takes from a closed gap to edge clips: VMS_BACKFILL_SETTLE_SECONDS (60) before it
# looks, a 30 s sweep to plan the job, the next to claim it, then the replay itself.
EDGE_FILL_TIMEOUT = 8 * 60
# The edge step's GOP: long, so that a replay asked to start between key frames comes from well before
# it, and footage stamped from where the replay began rather than from the camera's own time shows.
EDGE_GOV_LENGTH = 80  # frames, 8 s at the lab's 10 fps
# How far the first recovered clip may sit from a key frame of the camera's recording: ta_vms knows the
# camera's clock only from GetSystemDateAndTime, which is in whole seconds.
EDGE_STAMP_TOLERANCE = 1.2
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


def state_file(vms_url):
    """One per ta_vms: the ids in it belong to that installation. The dev stack's keeps the old name."""
    url = urllib.parse.urlsplit(vms_url)
    if url.hostname in ("localhost", "127.0.0.1"):
        return STATE_DIR / "state.json"
    return STATE_DIR / f"state-{url.hostname}-{url.port or 80}.json"


def load_state(path):
    """What the steps created, by pair: {"map": {"space_id", "landmarks"}, "plan": {...}}."""
    state = json.loads(path.read_text()) if path.exists() else {}
    # Before there were two pairs the map's entries lay at the top; a bench provisioned then keeps
    # its map and its calibration.
    if "space_id" in state:
        state = {W.MAP_PAIR.name: {"space_id": state["space_id"], "landmarks": state.get("landmarks", {})}}
    return state


def save_state(path, state):
    STATE_DIR.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(state, indent=2))


class Lab:
    def __init__(self, args):
        self.args = args
        self.sim = Sim(f"http://127.0.0.1:{args.sim_port}")
        self.vms = Vms(args.vms, args.user, args.password)
        self.state_file = state_file(args.vms)
        self.state = load_state(self.state_file)
        self.hang(W.MOUNTS_BY_NAME[args.mount])
        self.pairs = [p for p in self.all_pairs if args.pair in ("both", p.name)]
        # What the steps measured, for the matrix's table; notes are thresholds a "measure" mount missed.
        self.metrics = {}

    def hang(self, mount):
        """Takes the map pair as hung by mount; the plan pair always stands level."""
        self.mount = mount
        self.all_pairs = [mount.apply(p) if p.name == W.MAP_PAIR.name else p for p in W.PAIRS]

    def expect(self, condition, message):
        """A quality threshold: fatal for a mount expected to pass, a note for one run to measure."""
        if condition:
            return
        if self.mount.expect != "measure":
            raise Failed(message)
        self.metrics.setdefault("notes", []).append(message)
        log(f"  note ({self.mount.name}): {message}")

    def cam_id(self, camera):
        return f"{self.args.prefix}{camera.id}"

    @property
    def onvif_base(self):
        return self.args.onvif_base_port

    def anchor(self, pair):
        """The WGS84 a pair's space measures in: the real anchor for the map, (0, 0) for the plan,
        which stands in made-up coordinates."""
        return (self.args.lat0, self.args.lon0) if pair.name == W.MAP_PAIR.name else (0.0, 0.0)

    def wgs84(self, pair, x, z):
        return W.to_wgs84(x, z, *self.anchor(pair))

    def of(self, pair):
        """What the steps created for a pair."""
        return self.state.setdefault(pair.name, {})

    def provisioned(self, pair):
        state = self.of(pair)
        require(state.get("space_id") and state.get("landmarks"), f"run provision for the {pair.name} pair first")
        return state

    # ── up / down ───────────────────────────────────────────────────────────────

    def up(self):
        if self.sim.ready():
            log("OnvifSim already running")
        else:
            self._start_sim()
        self.sim.put_world(W.build_world(self.onvif_base, self.all_pairs))
        status = self.sim.status()
        require(status.get("h264"), "OnvifSim offers no H.264 (ffmpeg not found): video_srv needs Profile_2")
        ports = status.get("onvifPorts", {})
        cameras = [*W.STREAMING_CAMERAS, W.TOPDOWN]
        expected = {c.id: self.onvif_base + i for i, c in enumerate(cameras)}
        require(ports == expected, f"OnvifSim ONVIF ports are {ports}, expected {expected}")
        log(f"OnvifSim world loaded: ONVIF {expected}, RTSP {status.get('rtspPort')}, map pair hung '{self.mount.name}'")

    def _compose(self, *command, check=True):
        a = self.args
        env = dict(os.environ,
                   ONVIFSIM_DIR=str(Path(a.sim_dir).resolve()), ONVIFSIM_PORT=str(a.sim_port),
                   ONVIFSIM_RTSP_PORT=str(a.rtsp_port), ONVIFSIM_RTP_PORT=str(a.rtp_port),
                   ONVIFSIM_RTP_PORT_END=str(a.rtp_port + 1),
                   ONVIFSIM_ONVIF_BASE_PORT=str(a.onvif_base_port),
                   # The cameras ta_vms talks to; the plan camera's port stays inside the container.
                   ONVIFSIM_ONVIF_LAST_PORT=str(a.onvif_base_port + len(W.STREAMING_CAMERAS) - 1))
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
            for pair in self.pairs:
                for camera in (pair.master, pair.slave):
                    try:
                        self.vms.delete_camera(self.cam_id(camera))
                        log(f"removed camera {self.cam_id(camera)}")
                    except HttpError as e:
                        log(f"camera {self.cam_id(camera)}: {e}")
                space_id = self.state.pop(pair.name, {}).get("space_id")
                if space_id:
                    self.vms.delete_space(space_id)
                    log(f"removed space {space_id}")
        save_state(self.state_file, self.state)

    def status(self):
        self._compose("ps", check=False)
        log(json.dumps(self.state, indent=2))

    # ── provision ───────────────────────────────────────────────────────────────

    def provision(self):
        self.vms.login()
        existing = {c["cameraId"] for c in self.vms.cameras()}
        for pair in self.pairs:
            for camera in (pair.master, pair.slave):
                self._ensure_camera(camera, existing)

        for pair in self.pairs:
            space_id = self._ensure_space(pair)
            landmarks = self._ensure_landmarks(pair, space_id)
            for camera in (pair.master, pair.slave):
                lat, lon = self.wgs84(pair, camera.x, camera.z)
                self.vms.upsert_placement(self.cam_id(camera), space_id, {
                    "cameraId": self.cam_id(camera), "spaceId": space_id, "isReference": True,
                    "lat": lat, "lon": lon, "heightM": camera.y, "headingDeg": camera.heading,
                    "hfovDeg": camera.hfov if camera.kind == "fixed" else pair.limits.fov_wide,
                    "rangeM": 60,
                })
            log(f"space '{SPACE_NAMES[pair.name]}' ({space_id}): {len(landmarks)} landmarks, "
                f"{self.cam_id(pair.master)} and {self.cam_id(pair.slave)} placed")
            self.of(pair).update(space_id=space_id, landmarks=landmarks)
            save_state(self.state_file, self.state)

        for pair in self.pairs:
            master = self.cam_id(pair.master)
            log(f"waiting for {master}'s person boxes to reach ta_vms (RTSP metadata -> VmsOnvif -> analytics)")
            boxes = self._master_boxes(pair, seconds=60, wanted=1)
            require(boxes, f"no ONVIF person box from {master} reached /api/analytics/live within 60 s: "
                           "check video_srv's log for 'ONVIF metadata track found' and VmsOnvif's for 'scene objects'")
            log(f"{master}'s detections arrive in ta_vms")

    def _ensure_camera(self, camera, existing):
        cam_id = self.cam_id(camera)
        if cam_id in existing and self.args.reset:
            self.vms.delete_camera(cam_id)
            existing.discard(cam_id)
        if cam_id in existing:
            log(f"camera {cam_id} already in ta_vms")
            return
        port = self.onvif_base + W.camera_index(camera)
        self.vms.add_camera({
            "cameraId": cam_id,
            "name": f"OnvifSim {camera.name}",
            "serverId": self.args.server_id,
            "onvifConn": {"ip": self.args.sim_host, "port": port,
                          "xaddr": f"http://{self.args.sim_host}:{port}/onvif/device_service"},
            "cred": {"user": "admin", "pass": "admin"},
            "hasTelemetry": camera.kind == "ptz",
            "streams": [{"streamName": "main", "profileToken": PROFILE, "path": "", "webRtc": True}],
        })
        log(f"added camera {cam_id} (ONVIF {self.args.sim_host}:{port}, {PROFILE})")

    def _ensure_space(self, pair):
        name = SPACE_NAMES[pair.name]
        space = next((s for s in self.vms.spaces() if s.get("name") == name and not s.get("parentId")), None)
        if space is None:
            if pair.name == W.MAP_PAIR.name:
                sw, ne = self.wgs84(pair, -100, 100), self.wgs84(pair, 100, -100)
            else:
                corners = W.plan_corners(*self.anchor(pair))
                sw, ne = (corners["bl"][0], corners["tl"][1]), (corners["tl"][0], corners["tr"][1])
            space = self.vms.upsert_space({
                "name": name, "kind": "map" if pair.name == W.MAP_PAIR.name else "plan",
                "realCoordinates": pair.name == W.MAP_PAIR.name, "ordinal": 0, "elevationM": 0,
                "view": {"southWest": {"lat": sw[0], "lon": sw[1]}, "northEast": {"lat": ne[0], "lon": ne[1]}},
                "footprint": [],
            })
        if pair.name == W.PLAN_PAIR.name and (space.get("plan") is None or self.args.reset):
            self._photograph_plan(pair, space["id"])
        return space["id"]

    def _photograph_plan(self, pair, space_id):
        """The plan's picture: the yard from the camera straight above it, with nobody in it and none
        of the cameras drawn."""
        self.sim.delete_walker(W.WALKER_ID)
        self.sim.delete_walker(MARK_WALKER)
        self.sim.put_environment(W.PLAN_ENVIRONMENT)
        try:
            image = self.sim.snapshot(W.TOPDOWN.id)
        finally:
            self.sim.put_environment(W.ENVIRONMENT)
            self.sim.put_walker(W.WALKER_ID, W.walker_json(W.WALKER_ROUTE))
        self.vms.upload_plan(space_id, image, "image/jpeg", W.PLAN_W, W.PLAN_H, W.plan_corners(*self.anchor(pair)))
        log(f"plan picture uploaded: {W.PLAN_W}x{W.PLAN_H}, {len(image) // 1024} KiB, "
            f"{2 * W.PLAN_HALF_WIDTH:.0f} m wide from {W.PLAN_ALTITUDE:.0f} m up")

    def _ensure_landmarks(self, pair, space_id):
        # Matched on the first word ("L1"), so a landmark renamed here keeps its id -- and with it
        # every observation and mark already recorded against it.
        by_name = {lm["name"].split()[0]: lm["id"] for lm in self.vms.landmarks(space_id) if lm["name"]}
        ids = {}
        for lm in W.LANDMARKS:
            lat, lon = self.wgs84(pair, lm.x, lm.z)
            body = {"spaceId": space_id, "name": lm.label, "lat": lat, "lon": lon, "heightM": 0}
            if lm.name in by_name:
                body["id"] = by_name[lm.name]
            ids[lm.name] = self.vms.upsert_landmark(body)["id"]
        return ids

    def _master_boxes(self, pair, seconds, wanted):
        """Person boxes of a pair's master, as ta_vms streams them, until `wanted` have arrived."""
        master = self.cam_id(pair.master)
        found = []
        for d in self.vms.live_detections(seconds):
            if d.get("cameraId") == master and d.get("source") == "onvif" and d.get("kind") == "person":
                found.append(d)
                if len(found) >= wanted:
                    break
        return found

    # ── calibrate ───────────────────────────────────────────────────────────────

    def calibrate(self, ptz=True, ground=True):
        self.vms.login()
        for pair in self.pairs:
            self.provisioned(pair)
        slaves = [self.cam_id(pair.slave) for pair in self.pairs]
        # A slave already following its master would turn towards the person standing on each
        # landmark while it is being calibrated. Held, as the calibration wizard holds it.
        for slave in slaves:
            self.vms.hold(slave, 300)
        self.sim.delete_walker(W.WALKER_ID)
        try:
            for pair in self.pairs:
                if ptz:
                    self._calibrate_ptz(pair)
                if ground:
                    self._calibrate_ground(pair)
        finally:
            self.sim.delete_walker(MARK_WALKER)
            self.sim.put_walker(W.WALKER_ID, W.walker_json(W.WALKER_ROUTE))
            for slave in slaves:
                try:
                    self.vms.hold(slave, 0)
                except HttpError as e:
                    # Best effort: the hold lapses by itself, and failing here would hide why the
                    # calibration stopped.
                    log(f"could not release the hold on {slave}: {e}")

    def _calibrate_ptz(self, pair):
        slave = self.cam_id(pair.slave)
        ids = self.of(pair)["landmarks"]
        observations = []
        log(f"PTZ calibration of {slave}: centre each landmark, read the position back over ONVIF")
        log("  lm   pan°     tilt°    | ONVIF pan  tilt    | expected pan tilt   | dist m  zoom")
        for lm in W.LANDMARKS:
            # The sim turns its own head onto the landmark, whichever way it hangs.
            try:
                pan, tilt = self.sim.aim(pair.slave.id, (lm.x, 0.0, lm.z))
            except HttpError as e:
                raise Failed(f"{lm.name} is beyond {pair.slave.id}'s head as hung '{self.mount.name}': {e}") from e
            self.sim.wait_idle(pair.slave.id)
            st = self.vms.ptz_status(slave, PROFILE)
            require(st.get("available"), f"GET /api/ptz/status for {slave}: {st}")
            require(not st.get("moving"), f"{slave} still reports MOVING after the sim head stopped")
            gx, gy = W.to_generic(pan, tilt, pair.limits)
            require(abs(st["pan"] - gx) < 1e-3 and abs(st["tilt"] - gy) < 1e-3,
                    f"ONVIF reports ({st['pan']:.4f}, {st['tilt']:.4f}) at {lm.name}, expected ({gx:.4f}, {gy:.4f})")
            observation = {"landmarkId": ids[lm.name], "pan": st["pan"], "tilt": st["tilt"]}
            # The framing zoom, as the wizard records it: zoom in on the landmark until a person
            # there would fill the picture as wanted, and read the zoom back from the camera.
            _, _, distance = W.bearing(pair.slave.position, (lm.x, 0.0, lm.z))
            zoom_note = "  -"
            if not self.args.no_zoom:
                zoom = W.framing_zoom(distance, pair.limits)
                self.sim.goto(pair.slave.id, pan, tilt, zoom)
                self.sim.wait_idle(pair.slave.id)
                zst = self.vms.ptz_status(slave, PROFILE)
                require(abs(zst["zoom"] - zoom) < 1e-3, f"ONVIF reports zoom {zst['zoom']:.4f} at {lm.name}, expected {zoom:.4f}")
                observation["zoom"] = zst["zoom"]
                zoom_note = f"{zst['zoom']:.4f}"
            log(f"  {lm.name}  {pan:7.2f}  {tilt:7.2f}  | {st['pan']:8.4f} {st['tilt']:7.4f} | {gx:8.4f} {gy:7.4f} "
                f"| {distance:6.1f}  {zoom_note}")
            observations.append(observation)

        result = self.vms.put_ptz_geo(slave, observations)
        require(result and result.get("fitted"), f"the PTZ geo fit of {slave} failed: {result and result.get('error')}")
        worst = max(max(abs(o.get("panResidual") or 0), abs(o.get("tiltResidual") or 0)) for o in result["observations"])
        self.metrics["ptz_residual"] = worst
        log(f"  fitted; worst leave-one-out residual {worst:.5f} (generic units)")
        self.expect(worst < 0.01, f"leave-one-out residual {worst:.4f} is too large for an exactly linear head")
        # The distances ta_vms measures the samples by, against the world's own.
        for o in result["observations"]:
            lm = next(m for m in W.LANDMARKS if ids[m.name] == o["landmarkId"])
            _, _, distance = W.bearing(pair.slave.position, (lm.x, 0.0, lm.z))
            require(o.get("distanceM") is not None and abs(o["distanceM"] - distance) < 0.1,
                    f"ta_vms puts {lm.name} {o.get('distanceM')} m from {slave}, the world {distance:.2f} m")
            require(self.args.no_zoom or o.get("zoom") is not None, f"the zoom sample at {lm.name} was not kept")
        self.sim.goto(pair.slave.id, 0.0, -10.0, 0.0)

    def _zoom_samples(self, pair):
        """The slave's zoom samples as ta_vms holds them: (distance m, zoom); empty when it has none."""
        calibration = self.vms.get_ptz_geo(self.cam_id(pair.slave))
        return [(o["distanceM"], o["zoom"]) for o in calibration["observations"]
                if o.get("zoom") is not None and o.get("distanceM") is not None]

    def _calibrate_ground(self, pair):
        master = self.cam_id(pair.master)
        ids = self.of(pair)["landmarks"]
        observations = []
        clicked = self.mount.marks == "clicked"
        log(f"ground marks of {master}: a person stands on each landmark; the mark is "
            + ("where the landmark's foot is in the picture, as an operator clicks it" if clicked else "the foot of the box ta_vms received"))
        log("  lm   x       y       | expected x  y       | samples")
        # Where the feet are in the picture, from the master's true pose: however it hangs and turns it.
        master_pose = self.sim.camera_pose(pair.master.id)
        worst_foot = 0.0
        for lm in W.LANDMARKS:
            self.sim.put_walker(MARK_WALKER, W.walker_json([(lm.x, lm.z)]))
            time.sleep(1.5)  # let boxes from the previous spot drain through the pipeline
            boxes = self._master_boxes(pair, seconds=6, wanted=8)[-5:]
            ex, ey = W.project_pose(master_pose, (lm.x, 0.0, lm.z), *pair.master.video)
            if len(boxes) < 3:
                self.expect(clicked, f"too few boxes from {master} for the person on {lm.name}: {len(boxes)}")
                log(f"  {lm.name}  -       -       | {ex:.4f}  {ey:.4f}  | {len(boxes)}")
            else:
                xs = [b["bboxX"] + b["bboxWidth"] / 2 for b in boxes]
                ys = [b["bboxY"] + b["bboxHeight"] for b in boxes]
                x, y = statistics.median(xs), statistics.median(ys)
                log(f"  {lm.name}  {x:.4f}  {y:.4f}  | {ex:.4f}  {ey:.4f}  | {len(boxes)}")
                worst_foot = max(worst_foot, abs(x - ex), abs(y - ey))
                self.metrics["foot_miss"] = worst_foot
                # Not data, a diagnosis: a mark this far off means the box's bottom is not where the feet are.
                self.expect(abs(x - ex) < 0.03 and abs(y - ey) < 0.03,
                            f"the box foot on {lm.name} is at ({x:.3f}, {y:.3f}), the picture puts the feet at ({ex:.3f}, {ey:.3f})")
            mark = (ex, ey) if clicked else (x, y)
            observations.append({"landmarkId": ids[lm.name], "x": mark[0], "y": mark[1]})

        result = self.vms.put_ground_geo(master, observations)
        require(result and result.get("fitted"), f"the ground fit of {master} failed: {result and result.get('error')}")
        residuals = [o.get("residualM") for o in result["observations"] if o.get("residualM") is not None]
        worst = max(residuals) if residuals else 0.0
        self.metrics["ground_residual"] = worst
        log(f"  fitted; worst residual {worst:.3f} m")
        self.expect(worst < 0.3, f"ground residual {worst:.2f} m is too large on a flat world without distortion")

    # ── aim-check ───────────────────────────────────────────────────────────────

    def aim_check(self):
        self.vms.login()
        for pair in self.pairs:
            self._aim_check(pair, self.provisioned(pair)["space_id"])

    def _aim_check(self, pair, space_id):
        slave = self.cam_id(pair.slave)
        self.sim.put_walker(W.WALKER_ID, W.walker_json(W.WALKER_ROUTE))
        samples = self._zoom_samples(pair)
        log(f"aim {slave} at the walking person through POST /api/ptz-geo/{{id}}/aim, {self.args.seconds:.0f} s, "
            f"{len(samples)} zoom sample(s)")
        log("   t    person x  z     | ta_vms az°  el°    | true az°  el°    | aim err°  lag err° | zoom  expected | in frame")
        aim_errors, lag_errors, framed, zoom_errors = [], [], [], []
        start = time.monotonic()
        while time.monotonic() - start < self.args.seconds:
            target = self.sim.walker_pose(W.WALKER_ID)
            feet = (target["x"], target["y"], target["z"])
            lat, lon = self.wgs84(pair, target["x"], target["z"])
            aim = self.vms.aim_world(slave, space_id, lat, lon, 0.0)
            pose = self.sim.wait_idle(pair.slave.id)
            cam = (pose["position"]["x"], pose["position"]["y"], pose["position"]["z"])
            axis = W.axis_of(pose)
            to_target = tuple(f - c for f, c in zip(feet, cam))
            true_az, true_el, true_dist = W.bearing(cam, feet)
            aim_err = W.angle_between(axis, to_target)
            # ta_vms's interpolation redone here from the same samples, the widest outside the landmarks.
            expected_zoom = (0.0 if not samples
                             else min(z for _, z in samples) if aim["extrapolated"]
                             else W.zoom_by_distance(samples, true_dist))

            now = self.sim.walker_pose(W.WALKER_ID)
            now_feet = (now["x"], now["y"], now["z"])
            lag_err = W.angle_between(axis, tuple(f - c for f, c in zip(now_feet, cam)))
            in_frame, _ = W.person_in_frame(pose, now_feet, pair.slave.video)

            aim_errors.append(aim_err)
            lag_errors.append(lag_err)
            framed.append(in_frame)
            zoom_errors.append(abs(pose["zoom"] - expected_zoom))
            log(f"{time.monotonic() - start:5.1f}  {target['x']:7.2f} {target['z']:7.2f} | "
                f"{aim['azimuthDeg']:8.2f} {aim['elevationDeg']:7.2f} | {true_az:8.2f} {true_el:7.2f} | "
                f"{aim_err:7.3f}  {lag_err:7.3f} | {pose['zoom']:.3f} {expected_zoom:.3f}  | {'yes' if in_frame else 'NO'}")
            time.sleep(max(0.0, self.args.interval - 1.0))

        require(aim_errors, "no aim was made")
        report = _stats("aim error (where it was sent)", aim_errors) + "\n" + _stats("lag error (person has moved on)", lag_errors)
        log(report)
        log(f"person wholly in {slave}'s picture: {sum(framed)}/{len(framed)}; worst zoom miss {max(zoom_errors):.4f}")
        mean = statistics.mean(aim_errors)
        p95 = _percentile(aim_errors, 95)
        self.metrics.update(aim_mean=mean, aim_p95=p95, aim_framed=sum(framed) / len(framed))
        self.expect(mean < 1.0 and p95 < 1.5, f"{slave}: aim error mean {mean:.2f}°, p95 {p95:.2f}°: expected under 1° / 1.5°")
        # Float on the wire and the distance of the feet against the floor's: well under a hundredth.
        self.expect(max(zoom_errors) < 0.03, f"{slave} was zoomed {max(zoom_errors):.3f} away from its samples")
        self.expect(sum(framed) / len(framed) > 0.9, f"the person is out of {slave}'s picture too often")
        log(f"aim-check of {slave} done")

    # ── bind / run ──────────────────────────────────────────────────────────────

    def bind(self):
        self.vms.login()
        for pair in self.pairs:
            if pair.name == W.PLAN_PAIR.name:
                self._check_plan_is_apart(pair)
            self._bind(pair)

    def _check_plan_is_apart(self, pair):
        """What the plan's pair is here for: a plan in made-up coordinates shares no geometry with
        the map, so a master on the map must not drive a slave on the plan, nor may the slave be
        aimed at a point of the map."""
        map_space = self.of(W.MAP_PAIR).get("space_id")
        if not map_space:
            log("the map pair is not provisioned: skipping the check that the plan is kept apart from it")
            return
        slave, foreign = self.cam_id(pair.slave), self.cam_id(W.MAP_PAIR.master)
        reason = "shares no coordinates"

        self.vms.put_binding(slave, {"masterCameraId": foreign, "method": "geo", "points": []})
        binding = self.vms.get_binding(slave)
        unusable = (binding or {}).get("unusable") or ""
        require(reason in unusable, f"{slave} bound to {foreign} on the map is not refused: {binding}")
        log(f"{slave} bound to {foreign}: refused as it should be ({unusable})")

        lat, lon = self.wgs84(W.MAP_PAIR, 0.0, 0.0)
        try:
            aim = self.vms.aim_world(slave, map_space, lat, lon, 0.0)
        except HttpError as e:
            require(e.status == 400 and reason in e.body, f"aiming {slave} at the map failed otherwise: {e}")
            log(f"{slave} aimed at a point of the map: refused as it should be")
            return
        raise Failed(f"{slave} on the plan was aimed at a point of the map: {aim}")

    def _bind(self, pair):
        master, slave = self.cam_id(pair.master), self.cam_id(pair.slave)
        self.vms.put_binding(slave, {"masterCameraId": master, "method": "geo", "points": []})
        binding = self.vms.get_binding(slave)
        require(binding and binding.get("method") == "geo", f"the binding of {slave} did not come back as geo: {binding}")
        require(not binding.get("unusable"), f"the geo binding of {slave} cannot aim: {binding.get('unusable')}")
        self.vms.put_policy(slave, {"enabled": True, "kinds": ["person"], "sources": ["onvif"]})
        log(f"{slave} follows {master} through the map; waiting for its session")

        deadline = time.monotonic() + 30
        state = None
        while time.monotonic() < deadline:
            state = next((s for s in self.vms.tracking_states() if s["cameraId"] == slave), None)
            if (state and state.get("active") and not state.get("lastError")
                    and state.get("masterCameraId") == master):
                log(f"session running: following master {state.get('masterCameraId')}")
                return
            time.sleep(1)
        raise Failed(f"no running session for {slave} after 30 s: {state}. "
                     "Is vmsptztrackingsingleton subscribed? scripts/check-subscriptions.py")

    def run(self):
        self.vms.login()
        self.sim.put_walker(W.WALKER_ID, W.walker_json(W.WALKER_ROUTE))
        for pair in self.pairs:
            self._run(pair)

    def _run(self, pair):
        slave = self.cam_id(pair.slave)
        seconds = self.args.seconds
        # aim-check leaves the slave held for 30 s after its last aim, as the map's aim does for an
        # operator; with only bind in between, run would otherwise start by watching it stand still.
        self.vms.hold(slave, 0)
        log(f"ta_vms follows the person with {slave} on its own for {seconds:.0f} s")

        zoomed = bool(self._zoom_samples(pair))
        log("   t     person x  z     | moving | error°   | zoom  fill | event")
        tracking, arrivals, framed, fills = [], [], [], []
        # A move is seen by the head's position changing, not only by its MOVING flag: a short slew
        # finishes between two samples.
        rest, started_at, prev_feet = None, None, None
        start = time.monotonic()
        while time.monotonic() - start < seconds:
            pose = self.sim.camera_pose(pair.slave.id)
            person = self.sim.walker_pose(W.WALKER_ID)
            cam = (pose["position"]["x"], pose["position"]["y"], pose["position"]["z"])
            axis = W.axis_of(pose)
            feet = (person["x"], person["y"], person["z"])
            err = W.angle_between(axis, tuple(f - c for f, c in zip(feet, cam)))
            # The person in the slave's picture, from the true pose: the slave has no analytics of
            # its own in this world, by design.
            in_frame, fill = W.person_in_frame(pose, feet, pair.slave.video)
            framed.append(in_frame)
            if in_frame:
                fills.append(fill)
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
                f"{'yes' if moving else ' - ':>6} | {err:7.3f}  | {pose['zoom']:.3f} {fill:4.2f} | {event}")
            prev_feet = feet
            time.sleep(0.25)

        state = next((s for s in self.vms.tracking_states() if s["cameraId"] == slave), {})
        require(arrivals, f"{slave} never moved; tracking state: {state}")
        log(_stats("arrival error (vs where the person was when the head set off)", arrivals))
        log(_stats("tracking error (head at rest vs the person now)", tracking))
        median_fill = statistics.median(fills) if fills else 0.0
        log(f"person wholly in {slave}'s picture: {sum(framed)}/{len(framed)} samples; "
            f"median share of its height {median_fill:.2f}")
        log(f"commands: {len(arrivals)}; tracking state: {json.dumps(state)}")

        mean, p95 = statistics.mean(arrivals), _percentile(arrivals, 95)
        self.metrics.update(arrival_mean=mean, arrival_p95=p95, tracking_p95=_percentile(tracking, 95),
                            run_framed=sum(framed) / len(framed), fill=median_fill)
        self.expect(mean < 1.5 and p95 < 2.5, f"{slave}: arrival error mean {mean:.2f}°, p95 {p95:.2f}°")
        self.expect(_percentile(tracking, 95) < 6, f"{slave}: tracking error p95 {_percentile(tracking, 95):.2f}°")
        self.expect(sum(framed) / len(framed) > 0.9, f"the person is out of {slave}'s picture too often")
        # Framed for FRAME_WIDTH_M the person fills about 40% of the picture's height; at the wide
        # end, under 10%. A quarter is clearly zoomed and leaves room for the frames in transit.
        self.expect(not zoomed or median_fill > 0.25,
                    f"{slave} is not zoomed in on the person: median share of the picture {median_fill:.2f}")
        log(f"run of {slave} done")


    # ── matrix ──────────────────────────────────────────────────────────────────

    # ── edge ────────────────────────────────────────────────────────────────────

    def edge(self):
        self.vms.login()
        pair = W.MAP_PAIR
        camera = pair.master
        cam_id = self.cam_id(camera)
        self._record_with_backfill(cam_id)
        require(self.sim.recording(camera.id)["recording"], f"OnvifSim's {camera.id} does not record on its own card")
        log(f"{cam_id}: recording continuously in ta_vms, backfill from the camera's own recording on")
        body = W.camera_json(camera, self.onvif_base + W.camera_index(camera), pair.limits)
        body["h264"]["govLength"] = EDGE_GOV_LENGTH
        self.sim.put_camera(body)
        try:
            self._edge(camera, cam_id, body["h264"]["govLength"] / body["h264"]["fps"])
        finally:
            self.sim.put_camera(W.camera_json(camera, self.onvif_base + W.camera_index(camera), pair.limits))

    def _edge(self, camera, cam_id, gop):
        log(f"{camera.id}: a key frame every {gop:.0f} s")
        # A live segment before the cut, so the hole has an archive on both sides.
        time.sleep(20)

        cut = time.time()
        self.sim.set_network(camera.id, False)
        log(f"{camera.id} off the network for {self.args.outage:.0f} s (it goes on recording)")
        try:
            time.sleep(self.args.outage)
        finally:
            self.sim.set_network(camera.id, True)
        log(f"{camera.id} back on the network")

        since = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(cut - 60))
        deadline = time.monotonic() + EDGE_FILL_TIMEOUT
        last = None
        while time.monotonic() < deadline:
            gap = next((g for g in self.vms.gaps(cam_id, since) if _seconds(g["startedTs"]) >= cut - 30), None)
            if gap is not None:
                seen = (gap["state"], gap["filledMs"], gap["attempts"], gap.get("message", ""))
                if seen != last:
                    log(f"  gap {gap['startedTs']} .. {gap.get('endedTs') or 'open'}: {gap['state']}, "
                        f"filled {gap['filledMs'] / 1000:.1f} s, attempts {gap['attempts']} {gap.get('message', '')}".rstrip())
                    last = seen
                if gap["state"] == "filled" and gap.get("endedTs"):
                    length = _seconds(gap["endedTs"]) - _seconds(gap["startedTs"])
                    require(gap["filledMs"] / 1000 >= 0.9 * length,
                            f"the gap is marked filled with {gap['filledMs'] / 1000:.1f} s of {length:.1f} s")
                    log(f"edge import filled the {length:.1f} s gap from the camera's recording")
                    self.metrics["edge"] = {"gap_s": length, "filled_s": gap["filledMs"] / 1000,
                                            "stamp_s": self._edge_stamp(camera, cam_id, gap, gop)}
                    return
                require(gap["state"] not in ("exhausted", "partial"), f"the gap ended {gap['state']}: {gap.get('message', '')}")
            time.sleep(10)
        raise Failed(f"the gap was not filled within {EDGE_FILL_TIMEOUT // 60} min of the camera coming back")

    def _edge_stamp(self, camera, cam_id, gap, gop):
        """How far the first recovered clip starts from a key frame of the camera's recording.

        A replay asked to start between key frames starts at the one before, and that is the frame the
        first recovered clip begins with. Stamped with the time the camera recorded it at, the clip
        starts on the recording's key frame grid, a GOP from where the recording began; stamped with the
        time asked for, anywhere in between.
        """
        start, end = _seconds(gap["startedTs"]), _seconds(gap["endedTs"])
        iso = lambda t: time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(t))
        clips = [c for c in self.vms.recordings(cam_id, iso(start - 2 * gop - 60), iso(end + 60)) if c.get("origin") == "edge"]
        require(clips, "ta_vms lists no recovered clip for the gap")
        first = min(_seconds(c["startTime"]) for c in clips)
        span = next((s for s in self.sim.recording(camera.id)["spans"]
                     if _seconds(s["from"]) <= first + EDGE_STAMP_TOLERANCE <= _seconds(s["to"])), None)
        require(span is not None, f"the first recovered clip starts at {iso(first)}, where {camera.id} recorded nothing")
        phase = (first - _seconds(span["from"])) % gop
        off = phase if phase < gop / 2 else phase - gop
        log(f"first recovered clip starts {start - first:.2f} s before the gap, {off:+.2f} s from a key frame "
            f"of the camera's recording")
        require(first <= start, f"the recovered footage starts {first - start:.2f} s after the gap does")
        require(abs(off) <= EDGE_STAMP_TOLERANCE,
                f"the first recovered clip starts {off:+.2f} s from the nearest key frame: "
                "it is not stamped with the time the camera recorded it at")
        return off

    def _record_with_backfill(self, cam_id):
        """Continuous recording on the camera's streams, and backfill from its own recording."""
        setup = next((c for c in self.vms.cameras() if c["cameraId"] == cam_id), None)
        require(setup is not None, f"{cam_id} is not in ta_vms: run provision first")
        profile = next((p for p in self.vms.recording_profiles() if p["id"] == CONTINUOUS), None)
        require(profile is not None, "ta_vms has no built-in continuous recording profile")
        stream = setup["streams"][0]["streamName"]
        if setup.get("edgeBackfillStream") == stream and all(
                (s.get("recordingProfile") or {}).get("id") == CONTINUOUS for s in setup["streams"]):
            return
        setup["edgeBackfillStream"] = stream
        for s in setup["streams"]:
            s["recordingProfile"] = profile
        self.vms.add_camera(setup)

    def matrix(self):
        names = self.args.mounts.split(",") if self.args.mounts else [m.name for m in W.MOUNTS]
        unknown = [n for n in names if n not in W.MOUNTS_BY_NAME]
        require(not unknown, f"no such mount: {', '.join(unknown)} (world.MOUNTS has {', '.join(W.MOUNTS_BY_NAME)})")
        level = W.MOUNTS_BY_NAME["level"]
        self.hang(level)
        self.pairs = self.all_pairs
        self.up()
        self.provision()
        rows = []
        # The master the ground marks in ta_vms were taken for; None when they are not to be trusted.
        ground_for = None
        try:
            for name in names:
                row, ground_for = self._matrix_row(W.MOUNTS_BY_NAME[name], ground_for)
                rows.append(row)
        finally:
            # Level and calibrated again, so the ordinary steps find the bench as they left it.
            log("\nhanging the map pair level again and calibrating it")
            pair = self._rehang(level)
            if pair.master != ground_for:
                self._calibrate_ground_afresh(pair)
            self._calibrate_ptz_afresh(pair)
        table = _matrix_table(rows)
        log("\n" + table)
        STATE_DIR.mkdir(parents=True, exist_ok=True)
        (STATE_DIR / "matrix.json").write_text(json.dumps(rows, indent=2, default=str))
        (STATE_DIR / "matrix.txt").write_text(table + "\n")
        log(f"\nwritten to {STATE_DIR / 'matrix.txt'} and matrix.json")
        unexpected = [r["mount"] for r in rows if r["expect"] == "pass" and (r["failures"] or r.get("notes"))]
        require(not unexpected, f"mounts expected to pass did not: {', '.join(unexpected)}")

    def _rehang(self, mount):
        """Hangs the map pair as mount says, in the running sim, and works on that pair alone."""
        self.hang(mount)
        pair = next(p for p in self.all_pairs if p.name == W.MAP_PAIR.name)
        self.pairs = [pair]
        for camera in (pair.master, pair.slave):
            self.sim.put_camera(W.camera_json(camera, self.onvif_base + W.camera_index(camera), pair.limits))
        return pair

    def _calibrate_ground_afresh(self, pair):
        """The master's marks dropped and taken again, once its boxes come through: a rehung master
        changes its stream."""
        self.vms.put_ground_geo(self.cam_id(pair.master), [])
        require(self._master_boxes(pair, seconds=60, wanted=3), f"no boxes from {self.cam_id(pair.master)} after rehanging it")
        self.calibrate(ptz=False)

    def _calibrate_ptz_afresh(self, pair):
        """The slave's observations dropped first, so a calibration that stops halfway cannot leave
        the previous mount's fit behind to be aimed with."""
        self.vms.put_ptz_geo(self.cam_id(pair.slave), [])
        self.calibrate(ground=False)

    def _matrix_row(self, mount, ground_for):
        """Runs every step for one mount; returns its row and the master the ground marks now fit."""
        log(f"\n══ {mount.name}: {mount.note} (expected to {mount.expect}) ══")
        pair = self._rehang(mount)
        self.metrics = {}
        row = {"mount": mount.name, "expect": mount.expect, "note": mount.note, "failures": []}

        def step(label, action):
            try:
                action()
                return True
            except (Failed, HttpError) as e:
                row["failures"].append(f"{label}: {e}")
                log(f"  {label} failed: {e}")
                return False

        if pair.master == ground_for:
            log(f"  {self.cam_id(pair.master)} hangs as before: its ground marks stand")
            ground = True
        else:
            ground = step("ground marks", lambda: self._calibrate_ground_afresh(pair))
        ptz = step("PTZ calibration", lambda: self._calibrate_ptz_afresh(pair))
        # A fit that missed its thresholds is still a fit: aim with it and see.
        fitted = ptz or bool(self.vms.get_ptz_geo(self.cam_id(pair.slave)).get("fitted"))
        if fitted:
            step("aim-check", lambda: self._aim_check(pair, self.provisioned(pair)["space_id"]))
        if fitted and ground and step("bind", lambda: self._bind(pair)):
            step("run", lambda: self._run(pair))
        row.update(self.metrics)
        return row, (pair.master if ground else None)


def _matrix_table(rows):
    """One line per mount. Foot and ground are blank where the master's marks were not taken again."""
    columns = [
        ("mount", 15, lambda r: r["mount"]),
        ("expect", 7, lambda r: r["expect"]),
        ("PTZ LOO", 7, lambda r: _num(r.get("ptz_residual"), ".4f")),
        ("foot", 5, lambda r: _num(r.get("foot_miss"), ".3f")),
        ("ground m", 8, lambda r: _num(r.get("ground_residual"), ".2f")),
        ("aim mean/p95°", 13, lambda r: f"{_num(r.get('aim_mean'), '.2f')}/{_num(r.get('aim_p95'), '.2f')}"),
        ("arrival mean/p95°", 17, lambda r: f"{_num(r.get('arrival_mean'), '.2f')}/{_num(r.get('arrival_p95'), '.2f')}"),
        ("track p95°", 10, lambda r: _num(r.get("tracking_p95"), ".2f")),
        ("framed", 6, lambda r: _num(r.get("run_framed", r.get("aim_framed")), ".0%")),
    ]

    def outcome(r):
        if r["failures"]:
            return f"FAILED {r['failures'][0]}"
        if r.get("notes"):
            return f"{len(r['notes'])} note(s), first: {r['notes'][0]}"
        return "ok"

    lines = ["  ".join(f"{title:<{width}}" for title, width, _ in columns) + "  outcome",
             "  ".join("-" * width for _, width, _ in columns) + "  -------"]
    for r in rows:
        lines.append("  ".join(f"{value(r):>{width}}" if i > 1 else f"{value(r):<{width}}"
                               for i, (_, width, value) in enumerate(columns)) + f"  {outcome(r)}")
    return "\n".join(lines)


def _seconds(iso):
    """Unix seconds of an ISO 8601 time as ta_vms writes it; one without an offset is UTC."""
    moment = datetime.datetime.fromisoformat(iso)
    return (moment if moment.tzinfo else moment.replace(tzinfo=datetime.timezone.utc)).timestamp()


def _num(value, fmt):
    return "-" if value is None else format(value, fmt)


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
    p.add_argument("step", choices=["up", "provision", "calibrate", "aim-check", "bind", "run", "edge", "down", "status", "all", "matrix"])
    p.add_argument("--vms", default=f"http://localhost:{env.get('WEB_VMS_PORT', '5134')}")
    p.add_argument("--sim-dir", default=str(REPO.parent / "OnvifSim"))
    p.add_argument("--sim-host", default="127.0.0.1",
                   help="the address ta_vms reaches the sim's cameras at: this machine's LAN address "
                        "when --vms is a ta_vms elsewhere")
    p.add_argument("--sim-port", type=int, default=8090)
    p.add_argument("--rtsp-port", type=int, default=8560)
    p.add_argument("--rtp-port", type=int, default=6980)
    p.add_argument("--onvif-base-port", type=int, default=8101)
    p.add_argument("--server-id", default="vms")
    p.add_argument("--user", default="admin")
    p.add_argument("--password", default="admin")
    p.add_argument("--prefix", default="sim-", help="ta_vms camera ids are <prefix>master, <prefix>ptz, ...")
    p.add_argument("--pair", choices=["both", *[pair.name for pair in W.PAIRS]], default="both",
                   help="which master/slave pair the steps work on: the map's, the plan's, or both")
    p.add_argument("--lat0", type=float, default=W.DEFAULT_LAT0)
    p.add_argument("--lon0", type=float, default=W.DEFAULT_LON0)
    p.add_argument("--seconds", type=float, default=60, help="aim-check and run duration")
    p.add_argument("--interval", type=float, default=3, help="aim-check: seconds between aims")
    p.add_argument("--outage", type=float, default=60, help="edge: how long the camera is off the network")
    p.add_argument("--reset", action="store_true",
                   help="provision: re-add cameras that already exist and photograph the plan again")
    p.add_argument("--purge", action="store_true", help="down: also remove the cameras and the spaces from ta_vms")
    p.add_argument("--mount", choices=list(W.MOUNTS_BY_NAME), default="level",
                   help="how the map pair hangs for the ordinary steps (world.MOUNTS)")
    p.add_argument("--mounts", help="matrix: a comma-separated subset of world.MOUNTS, in that order")
    p.add_argument("--no-zoom", action="store_true",
                   help="calibrate: record no zoom samples, so the slaves stay wide as they did before zoom")
    args = p.parse_args()

    W.check()
    lab = Lab(args)
    steps = {
        "up": [lab.up], "provision": [lab.provision], "calibrate": [lab.calibrate],
        "aim-check": [lab.aim_check], "bind": [lab.bind], "run": [lab.run], "edge": [lab.edge],
        "down": [lab.down], "status": [lab.status], "matrix": [lab.matrix],
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
