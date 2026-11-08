"""The small test world: two master/slave pairs, five landmarks and one walking person, with some
scenery around them to find one's way by, and a camera high above that photographs it all as a plan.

The first pair stands on a map with real coordinates; the second on a plan in made-up ones, the
picture from the camera above. Both watch the same yard, the same landmarks and the same person.

Everything the harness knows about the world comes from here, so the world OnvifSim renders and
the geometry the checks compare against cannot drift apart.

OnvifSim's coordinates are metres with X east, Y up and Z south (north is -Z); in ENU that is
E = X, N = -Z, U = Y. The terrain is flat at zero everywhere, so every height is absolute.

Run it directly to print the world JSON and the expected numbers:

    python3 tests/onvifsim/world.py            # summary
    python3 tests/onvifsim/world.py --dump     # the world JSON OnvifSim is given
"""

import base64
import dataclasses
import json
import math
import struct
import sys
from dataclasses import dataclass, field
from pathlib import Path

# WGS84 anchor of the sim's origin: open farmland in Maryland, which also makes a presentable map.
# Not on the equator, so a cos(latitude) left out anywhere shows up as an east-west error instead
# of cancelling out.
DEFAULT_LAT0 = 39.2
DEFAULT_LON0 = -77.35

# The radius ta_vms's EnuFrame uses; the same formula, so the two agree to rounding.
EARTH_RADIUS_M = 6378137.0


@dataclass(frozen=True)
class Camera:
    id: str
    name: str
    kind: str  # "fixed" | "ptz"
    x: float
    y: float  # height above the (flat, zero) terrain
    z: float
    heading: float  # degrees clockwise from north
    pitch: float  # degrees, up positive; for a PTZ camera, how far its pan axis leans
    hfov: float = 70.0  # fixed cameras; a PTZ camera's comes from its zoom
    # Whether the camera boxes people in its metadata track. Only the master's boxes are used;
    # the slave's would be drawn over its video and mean nothing to the aiming.
    analytics: bool = True
    # The mount turned about its own axis (180: upside down, 90: on its side), and the picture turned
    # back digitally, clockwise (OnvifSim's video.rotate); see MOUNTS.
    roll: float = 0.0
    rotate: int = 0
    video: tuple = (640, 360)
    # Its body and the wire frame of where it looks in the other cameras' pictures (OnvifSim's
    # color): one colour each, so they can be told apart.
    color: str | None = None

    @property
    def position(self):
        return (self.x, self.y, self.z)


@dataclass(frozen=True)
class PtzLimits:
    pan_min: float = -180.0
    pan_max: float = 180.0
    tilt_min: float = -90.0
    tilt_max: float = 20.0
    fov_wide: float = 60.0
    fov_tele: float = 3.0
    # ONVIF counts the axis the other way round (OnvifSim's ptz.panReverse / tiltReverse).
    pan_reverse: bool = False
    tilt_reverse: bool = False


@dataclass(frozen=True)
class Landmark:
    name: str
    x: float
    z: float
    color: str = "#e8452c"
    color_name: str = "red"

    @property
    def label(self):
        """The name ta_vms shows: the colour makes a landmark on the map easy to find in the picture."""
        return f"{self.name} {self.color_name}"


MASTER = Camera("master", "Master (fixed)", "fixed", 0.0, 6.0, 20.0, heading=0.0, pitch=-18.0, hfov=70.0,
                color="#3aa0ff")
SLAVE = Camera("ptz", "Slave (PTZ)", "ptz", 28.0, 9.0, -14.0, heading=240.0, pitch=0.0, analytics=False,
               color="#ff9f1c")
# The plan's pair, from the other side of the yard.
MASTER2 = Camera("master2", "Master 2 (fixed)", "fixed", -26.0, 6.0, -34.0, heading=134.0, pitch=-18.0, hfov=70.0,
                 color="#b15cff")
SLAVE2 = Camera("ptz2", "Slave 2 (PTZ)", "ptz", -24.0, 8.0, 18.0, heading=45.0, pitch=0.0, analytics=False,
                color="#ff3b6b")
SLAVE_LIMITS = PtzLimits()

# Not green: the grass is.
LANDMARKS = [
    Landmark("L1", -9.0, 4.0, "#e53935", "red"),
    Landmark("L2", 9.0, 4.0, "#fdd835", "yellow"),
    Landmark("L3", -13.0, -12.0, "#1e88e5", "blue"),
    Landmark("L4", 13.0, -12.0, "#f5f5f5", "white"),
    Landmark("L5", 0.0, -24.0, "#8e24aa", "purple"),
]
LANDMARK_SIZE = 0.5  # metres; small enough that a person standing on one is still seen

VIDEO_W, VIDEO_H = 640, 360

# The plan's picture: a camera straight above the yard, looking down, heading north, so the top of
# its frame is north and its right is east. High and narrow, so that what stands up from the ground
# leans out little; its far corner stays nearer than the 150 m where OnvifSim's fog begins.
PLAN_W, PLAN_H = 1280, 960
PLAN_CENTRE = (0.0, -6.0)  # x, z
PLAN_ALTITUDE = 120.0
PLAN_HALF_WIDTH = 48.0  # metres on the ground; the half-height follows from the frame's aspect
TOPDOWN = Camera(
    "topdown", "Plan camera", "fixed", PLAN_CENTRE[0], PLAN_ALTITUDE, PLAN_CENTRE[1], heading=0.0, pitch=-90.0,
    hfov=2 * math.degrees(math.atan(PLAN_HALF_WIDTH / PLAN_ALTITUDE)), analytics=False, video=(PLAN_W, PLAN_H),
    # Pale: it covers the whole yard, and is the harness's camera rather than one under test.
    color="#d0d0d0")


@dataclass(frozen=True)
class Pair:
    """A master and the PTZ slave it drives, standing in one ta_vms space."""
    name: str  # "map" | "plan" in the yard, "hall" | "road" in the demo world
    master: Camera
    slave: Camera
    limits: PtzLimits = SLAVE_LIMITS
    # The landmarks one pair is calibrated on. The yard's two pairs watch the same yard; the demo's
    # hall and road are apart, and each has its own.
    landmarks: tuple = field(default_factory=lambda: tuple(LANDMARKS))
    # What the master's boxes aim the slave at: the walking person, in both demo pairs.
    target_kinds: tuple = ("person",)
    # The demo space the pair stands in: its own field because a pair's cameras are placed by the
    # demo world, and the bench has to know which space to look for.
    space: str | None = None


MAP_PAIR = Pair("map", MASTER, SLAVE)
PLAN_PAIR = Pair("plan", MASTER2, SLAVE2)
PAIRS = [MAP_PAIR, PLAN_PAIR]
# The cameras ta_vms gets, in the order of their ONVIF ports.
STREAMING_CAMERAS = [c for pair in PAIRS for c in (pair.master, pair.slave)]


def camera_index(camera):
    """Where a camera (or a mount variant of it, which keeps its id) stands in STREAMING_CAMERAS."""
    return next(i for i, c in enumerate(STREAMING_CAMERAS) if c.id == camera.id)

WALKER_ID = "w1"
WALKER_ROUTE = [(-8.0, 0.0), (8.0, 0.0), (8.0, -16.0), (-8.0, -16.0)]
WALKER_SPEED = 1.0
WALKER_HEIGHT = 1.75

# How wide the slave's picture is at the person, as the calibration frames it at each landmark:
# the person, plus how far they walk between two commands (ta_vms sends one every 2 s at most, so
# 2 m at WALKER_SPEED) either side, plus room for the aim's own error. With a 16:9 picture that is
# 4.5 m tall, so a person fills about 40% of its height -- against under 10% at the wide end 30 m
# away.
FRAME_WIDTH_M = 8.0


@dataclass(frozen=True)
class Scenery:
    """Something to find one's way by. Kept off every line of sight that matters (see check)."""
    id: str
    type: str  # OnvifSim object types: tree.pine, tree.oak, house, rock
    x: float
    z: float
    sx: float
    sy: float
    sz: float
    rotation: float = 0.0
    color: str = None
    color2: str = None

    @property
    def radius(self):
        """How far from its centre it reaches on the ground, rounded up."""
        return math.hypot(self.sx, self.sz) / 2 + 0.3


SCENERY = [
    # A row of pines closing the far side of the yard, behind everything the cameras look at.
    *[Scenery(f"pine{i}", "tree.pine", x, -42.0, 3.8, 10.0 + (i % 3) * 1.5, 3.8) for i, x in enumerate(range(-30, 31, 8))],
    # Houses: one west of the yard behind the slave's view, one north-east, one south-west.
    Scenery("house-west", "house", -38.0, -8.0, 8.0, 5.0, 10.0, 90, "#d8c9a8", "#8a3b2e"),
    Scenery("house-ne", "house", 28.0, -34.0, 10.0, 6.0, 8.0, 15, "#e6e0d4", "#3e5468"),
    Scenery("house-sw", "house", -30.0, 20.0, 9.0, 5.0, 8.0, 0, "#d9b8a0", "#7a5230"),
    # Oaks and rocks at the edges.
    Scenery("oak-w", "tree.oak", -30.0, -26.0, 6.0, 8.0, 6.0),
    Scenery("oak-e", "tree.oak", 36.0, 6.0, 6.0, 8.0, 6.0),
    Scenery("oak-s", "tree.oak", 18.0, 24.0, 5.0, 7.0, 5.0),
    Scenery("rock-1", "rock", -22.0, 6.0, 1.6, 1.0, 1.4),
    Scenery("rock-2", "rock", 22.0, -22.0, 2.0, 1.2, 1.6),
]

# Roads are drawn on the ground and hide nothing: a footpath along the walker's loop, and a lane
# behind the master (in front of it, a road filled the bottom third of its picture).
ROADS = [
    {"id": "path", "points": [*WALKER_ROUTE, WALKER_ROUTE[0]], "width": 1.6, "color": "#c9b48a"},
    {"id": "lane", "points": [(-60.0, 30.0), (60.0, 30.0)], "width": 4.0, "color": "#8c8c8c"},
]

TERRAIN_SIZE = 400.0
TERRAIN_RESOLUTION = 32
USERS = [{"username": "admin", "password": "admin"}]


# ── Geometry ────────────────────────────────────────────────────────────────────


def wrap180(deg):
    return (deg + 180.0) % 360.0 - 180.0


def to_wgs84(x, z, lat0=DEFAULT_LAT0, lon0=DEFAULT_LON0):
    """A sim ground point in WGS84, by EnuFrame's equirectangular formula around the anchor."""
    east, north = x, -z
    per_deg_lat = EARTH_RADIUS_M * math.pi / 180.0
    per_deg_lon = per_deg_lat * math.cos(math.radians(lat0))
    return lat0 + north / per_deg_lat, lon0 + east / per_deg_lon


def plan_corners(lat0=0.0, lon0=0.0):
    """Where the plan picture's top-left, top-right and bottom-left corners lie on the ground, in
    the made-up WGS84 of a plan anchored at (lat0, lon0): the frame's edges are exactly at
    +-tan(hfov/2), and the ground is flat at zero."""
    half_w = PLAN_HALF_WIDTH
    half_h = half_w * PLAN_H / PLAN_W
    cx, cz = PLAN_CENTRE
    north, south, west, east = cz - half_h, cz + half_h, cx - half_w, cx + half_w
    return {
        "tl": to_wgs84(west, north, lat0, lon0),
        "tr": to_wgs84(east, north, lat0, lon0),
        "bl": to_wgs84(west, south, lat0, lon0),
    }


def bearing(frm, to):
    """(azimuth deg cw from north in [0,360), elevation deg, distance m) from one sim point to another."""
    de, dn, du = to[0] - frm[0], -(to[2] - frm[2]), to[1] - frm[1]
    horizontal = math.hypot(de, dn)
    az = math.degrees(math.atan2(de, dn)) % 360.0
    return az, math.degrees(math.atan2(du, horizontal)), math.sqrt(horizontal ** 2 + du ** 2)


def direction(heading, pitch):
    """Unit vector in sim coordinates for a compass heading and a pitch (OnvifSim CameraPose.Basis)."""
    h, p = math.radians(heading), math.radians(pitch)
    return (math.sin(h) * math.cos(p), math.sin(p), -math.cos(h) * math.cos(p))


def angle_between(a, b):
    dot = sum(x * y for x, y in zip(a, b))
    na = math.sqrt(sum(x * x for x in a))
    nb = math.sqrt(sum(x * x for x in b))
    return math.degrees(math.acos(max(-1.0, min(1.0, dot / (na * nb)))))


def ptz_degrees(camera, target):
    """Pan and tilt (degrees, relative to the mount) that centre target, for a level mount only:
    check() and summary() use it on the base world. Any other mount is aimed by OnvifSim itself
    (Sim.aim), which knows how its mounts turn."""
    az, el, _ = bearing(camera.position, target)
    return wrap180(az - camera.heading), el - camera.pitch


def to_generic(pan, tilt, limits=SLAVE_LIMITS):
    """OnvifSim's ONVIF generic position for a head in degrees (CameraPtz.ToGeneric), a reversed
    axis counted the other way round."""
    span = 360.0 if limits.pan_max - limits.pan_min >= 360.0 else limits.pan_max - limits.pan_min
    x = (pan - limits.pan_min) / span * 2.0 - 1.0
    y = (tilt - limits.tilt_min) / (limits.tilt_max - limits.tilt_min) * 2.0 - 1.0
    return (-x if limits.pan_reverse else x), (-y if limits.tilt_reverse else y)


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def _dot(a, b):
    return sum(x * y for x, y in zip(a, b))


def level_pose(camera):
    """A pose as OnvifSim's /pose reports one, for a camera standing as configured with no roll and
    no rotation: what check() and summary() need before any simulator runs."""
    fwd = direction(camera.heading, camera.pitch)
    h = math.radians(camera.heading)
    right = (math.cos(h), 0.0, math.sin(h))
    vec = lambda v: {"x": v[0], "y": v[1], "z": v[2]}  # noqa: E731
    return {"position": vec(camera.position), "forward": vec(fwd), "right": vec(right), "up": vec(_cross(right, fwd)),
            "hfov": camera.hfov}


def project_pose(pose, point, width=VIDEO_W, height=VIDEO_H):
    """Where a sim point lands in the picture of a camera at a pose (OnvifSim's /pose: the picture's
    own axes, so any mount and rotation), 0..1 from the top-left; None behind it."""
    axis = lambda name: (pose[name]["x"], pose[name]["y"], pose[name]["z"])  # noqa: E731
    fwd, right, up, eye = axis("forward"), axis("right"), axis("up"), axis("position")
    d = tuple(p - c for p, c in zip(point, eye))
    depth = _dot(d, fwd)
    if depth <= 0:
        return None
    tan_h = math.tan(math.radians(pose["hfov"] / 2.0))
    tan_v = tan_h / (width / height)
    return (_dot(d, right) / depth / tan_h + 1.0) / 2.0, (1.0 - _dot(d, up) / depth / tan_v) / 2.0


def project(camera, point, width=VIDEO_W, height=VIDEO_H):
    """project_pose for a camera standing level as configured."""
    return project_pose(level_pose(camera), point, width, height)


def axis_of(pose):
    """Where a camera at a pose (OnvifSim's /pose) looks, as a unit vector in sim coordinates."""
    return pose["forward"]["x"], pose["forward"]["y"], pose["forward"]["z"]


def zoom_to_hfov(zoom, limits=SLAVE_LIMITS):
    """OnvifSim's lens (CameraPose.ZoomToHFov): zoom 0..1 steps the focal length by equal ratios
    from the wide end to the tele end."""
    fw = 1 / math.tan(math.radians(limits.fov_wide / 2))
    ft = 1 / math.tan(math.radians(limits.fov_tele / 2))
    f = fw * (ft / fw) ** min(1.0, max(0.0, zoom))
    return 2 * math.degrees(math.atan(1 / f))


def hfov_to_zoom(hfov, limits=SLAVE_LIMITS):
    """The zoom that gives a horizontal field of view, clamped to the lens."""
    fw = 1 / math.tan(math.radians(limits.fov_wide / 2))
    ft = 1 / math.tan(math.radians(limits.fov_tele / 2))
    f = 1 / math.tan(math.radians(hfov / 2))
    return min(1.0, max(0.0, math.log(f / fw) / math.log(ft / fw)))


def framing_zoom(distance, limits=SLAVE_LIMITS):
    """The zoom an operator would record at a landmark this far away: FRAME_WIDTH_M across."""
    return hfov_to_zoom(2 * math.degrees(math.atan(FRAME_WIDTH_M / 2 / distance)), limits)


def zoom_by_distance(samples, distance):
    """ta_vms's ZoomByDistance: piecewise linear in ln(distance) between (distance, zoom) samples,
    held at the ends."""
    samples = sorted(samples)
    if distance <= samples[0][0]:
        return samples[0][1]
    if distance >= samples[-1][0]:
        return samples[-1][1]
    for (d0, z0), (d1, z1) in zip(samples, samples[1:]):
        if d0 < distance <= d1:
            return z0 if d1 <= d0 else z0 + (z1 - z0) * math.log(distance / d0) / math.log(d1 / d0)
    return samples[-1][1]


def person_in_frame(pose, feet, size=(VIDEO_W, VIDEO_H), height=WALKER_HEIGHT):
    """Where a standing person is in a camera's picture, for a pose as OnvifSim reports it:
    (whether both their feet and their head are inside it, how much of the picture they span from
    feet to head -- its height for an upright picture, its width for one turned on its side)."""
    low = project_pose(pose, feet, *size)
    high = project_pose(pose, (feet[0], feet[1] + height, feet[2]), *size)
    if low is None or high is None:
        return False, 0.0
    inside = all(0.0 <= v <= 1.0 for v in (*low, *high))
    return inside, max(abs(low[0] - high[0]), abs(low[1] - high[1]))


def feet_in_frame(pose, feet, size=(VIDEO_W, VIDEO_H)):
    """Whether a person's feet are inside a camera's picture — the ground point an aim is sent to.

    A person who walks under a camera cannot be wholly inside any lens: at 2 m their head and feet
    are further apart than the picture is tall. The feet are what the aiming chain follows, so a
    follow is checked on them."""
    uv = project_pose(pose, feet, *size)
    return uv is not None and 0.0 <= uv[0] <= 1.0 and 0.0 <= uv[1] <= 1.0



# ── Mounts: the ways a camera can hang ─────────────────────────────────────────


@dataclass(frozen=True)
class Mount:
    """One way the map pair's cameras can be hung, for the matrix: overrides of the slave, of its
    PTZ limits and of the master. `expect` is "pass" when every check of every step must hold, or
    "measure" when the variant is run for its numbers: what the current fits make of a mount they
    are known not to model."""
    name: str
    expect: str
    note: str
    # How the master's ground marks are taken: "boxes", the foot of the box of a person standing on
    # each landmark (which also checks the box pipeline); or "clicked", where the landmark's foot
    # really is in the picture, as an operator clicks it in the calibration dialog.
    marks: str = "boxes"
    slave: dict = field(default_factory=dict)
    limits: dict = field(default_factory=dict)
    master: dict = field(default_factory=dict)

    def apply(self, pair):
        return dataclasses.replace(
            pair,
            master=dataclasses.replace(pair.master, **self.master),
            slave=dataclasses.replace(pair.slave, **self.slave),
            limits=dataclasses.replace(pair.limits, **self.limits),
        )


def _seam_heading():
    """A slave heading that puts its pan seam (pan = +-180) in the middle of the landmarks: the
    camera's own zero points away from them."""
    azimuths = [math.radians(bearing(SLAVE.position, (lm.x, 0.0, lm.z))[0]) for lm in LANDMARKS]
    mean = math.degrees(math.atan2(sum(map(math.sin, azimuths)), sum(map(math.cos, azimuths))))
    return (mean + 180.0) % 360.0


# Hung upside down, the head looks down with positive tilt: its range turns over with it.
_UPSIDE_DOWN_TILT = {"tilt_min": -20.0, "tilt_max": 90.0}

MOUNTS = [
    Mount("level", "pass", "upright on a pole, as the bench has always been"),
    Mount("pan-reverse", "pass", "ONVIF pan counted the other way (anticlockwise)", limits={"pan_reverse": True}),
    Mount("tilt-reverse", "pass", "ONVIF tilt counted the other way (down is positive)", limits={"tilt_reverse": True}),
    Mount("install-mode", "pass", "upside down, the installer turned the picture and both ONVIF axes back",
          slave={"roll": 180.0, "rotate": 180}, limits={**_UPSIDE_DOWN_TILT, "pan_reverse": True, "tilt_reverse": True}),
    Mount("auto-flip", "pass", "upside down, the picture turned back but ONVIF pan and tilt left mirrored",
          slave={"roll": 180.0, "rotate": 180}, limits=_UPSIDE_DOWN_TILT),
    Mount("upside-down-raw", "pass", "upside down and nothing turned back: the picture is on its head",
          slave={"roll": 180.0}, limits=_UPSIDE_DOWN_TILT),
    Mount("lean-forward", "measure", "the pan axis leaning 10 degrees towards the yard (a sagging bracket)",
          slave={"pitch": -10.0}),
    Mount("lean-sideways", "measure", "the pan axis leaning 8 degrees sideways", slave={"roll": 8.0}),
    Mount("fence", "measure", "on its side: pan sweeps up and down, tilt across",
          slave={"roll": 90.0, "rotate": 90}, limits={"tilt_min": -90.0, "tilt_max": 90.0}),
    Mount("seam", "pass", "turned so that the pan seam falls among the landmarks", slave={"heading": _seam_heading()}),
    # Clicked marks: in a frame this tall a person beyond about 28 m is smaller than OnvifSim's smallest
    # box (2.5% of the frame's height), so the far landmarks could not be marked by a person's box.
    Mount("master-portrait", "measure", "master on its side for a corridor, picture turned upright and tall",
          marks="clicked", master={"roll": 90.0, "rotate": 90, "video": (360, 640)}),
    # Clicked marks, as an operator takes them: the box's foot, which the aim follows, is then the head.
    Mount("master-flip", "measure", "master upside down with its picture left on its head",
          marks="clicked", master={"rotate": 180}),
]
MOUNTS_BY_NAME = {m.name: m for m in MOUNTS}


# ── The world OnvifSim is given ─────────────────────────────────────────────────


def _flat_terrain():
    n = (TERRAIN_RESOLUTION + 1) ** 2
    heights = base64.b64encode(struct.pack(f"<{n}f", *([0.0] * n))).decode()
    return {"size": TERRAIN_SIZE, "resolution": TERRAIN_RESOLUTION, "heights": heights}


def camera_json(camera, onvif_port, limits=SLAVE_LIMITS):
    """A camera as OnvifSim's world.json and PUT /api/cameras/{id} take it."""
    width, height = camera.video
    video = {"width": width, "height": height, "fps": 10, "quality": 70, "rotate": camera.rotate}
    body = {
        "id": camera.id,
        "name": camera.name,
        "kind": camera.kind,
        "position": {"x": camera.x, "y": camera.y, "z": camera.z},
        "heading": camera.heading,
        "pitch": camera.pitch,
        "roll": camera.roll,
        "hFov": camera.hfov,
        "color": camera.color,
        "video": video,
        "h264": {"width": width, "height": height, "fps": 10, "bitrateKbps": 1024, "govLength": 10},
        "metadata": {"analytics": camera.analytics, "ptzStatus": False},
        "onvifPort": onvif_port,
        "users": USERS,
    }
    if camera.kind == "ptz":
        lim = limits
        body["ptz"] = {
            "panMin": lim.pan_min, "panMax": lim.pan_max, "tiltMin": lim.tilt_min, "tiltMax": lim.tilt_max,
            "fovWide": lim.fov_wide, "fovTele": lim.fov_tele,
            "panReverse": lim.pan_reverse, "tiltReverse": lim.tilt_reverse,
            "pan": 0, "tilt": -10, "zoom": 0, "home": {"pan": 0, "tilt": -10, "zoom": 0}, "presets": [],
        }
    return body


def walker_json(route, speed=WALKER_SPEED):
    return {
        "route": [{"x": x, "z": z} for x, z in route],
        "speed": speed,
        "return": "loop",
        "height": WALKER_HEIGHT,
    }


# Each camera's wire frame in the others' pictures, to see where a head really looks; the rest is
# OnvifSim's defaults.
ENVIRONMENT = {"showFrustums": True}
# The plan's picture is a photograph of the site, with no cameras or wire frames in it.
PLAN_ENVIRONMENT = {**ENVIRONMENT, "showCameras": False, "showFrustums": False}


def build_world(onvif_base_port, pairs=None, with_walker=True):
    """The whole world; pairs, when given, replace PAIRS (a mount variant of one of them), in
    PAIRS' order so the ONVIF ports stay where they are."""
    pairs = pairs or PAIRS
    landmarks = [
        {
            "id": f"lm-{lm.name}", "type": "box",
            "position": {"x": lm.x, "y": 0, "z": lm.z},
            "rotationY": 0, "scale": {"x": LANDMARK_SIZE, "y": LANDMARK_SIZE, "z": LANDMARK_SIZE},
            "color": lm.color, "seed": i + 1,
        }
        for i, lm in enumerate(LANDMARKS)
    ]
    scenery = [
        {
            "id": s.id, "type": s.type, "position": {"x": s.x, "y": 0, "z": s.z}, "rotationY": s.rotation,
            "scale": {"x": s.sx, "y": s.sy, "z": s.sz}, "color": s.color, "color2": s.color2, "seed": 100 + i,
        }
        for i, s in enumerate(SCENERY)
    ]
    roads = [
        {"id": r["id"], "points": [{"x": x, "z": z} for x, z in r["points"]], "width": r["width"], "color": r["color"]}
        for r in ROADS
    ]
    walkers = [{"id": WALKER_ID, **walker_json(WALKER_ROUTE)}] if with_walker else []
    return {
        "version": 1,
        "environment": ENVIRONMENT,
        "terrain": _flat_terrain(),
        "objects": landmarks + scenery,
        "roads": roads,
        "walkers": walkers,
        "cameras": [
            *[camera_json(camera, onvif_base_port + camera_index(camera), pair.limits)
              for pair in pairs for camera in (pair.master, pair.slave)],
            # Never added to ta_vms: the harness only takes a snapshot from it. OnvifSim gives every
            # camera an ONVIF port all the same, the next one, which nothing maps out of the container.
            camera_json(TOPDOWN, onvif_base_port + len(STREAMING_CAMERAS)),
        ],
    }


# ── The demo world: Maryland ─────────────────────────────────────────────────────
#
# ta_vms's demo world (dotnet/VmsMap/Demo/DemoWorlds.cs) places its cameras on the first floor of
# Biomolecular Sciences on the University of Maryland campus — the real building, at the place
# OpenStreetMap puts it. OnvifSim builds that world from the same constants and ships it as
# data/worlds/maryland.json (World/MarylandWorld.cs); the bench loads that file rather than keeping a
# second copy of the geometry, and checks the cameras and the building in it against the constants
# below. Those mirror DemoWorlds.cs — the building's footprint, the doorway and the four camera
# poses — so keep the two in step. The demo has two pairs, both working on the same walking person:
# the hall's master watches the line from its slave out to the doorway, and the yard pair works the
# parking lot outside it. Every master detects the person; every slave reports its head in the
# metadata track.

REPO = Path(__file__).resolve().parents[2]

MARYLAND_LAT0 = 38.9923  # the demo world's anchor
MARYLAND_LON0 = -76.9378
MARYLAND_POSITION_TOLERANCE_M = 0.5
MARYLAND_ANGLE_TOLERANCE_DEG = 0.5
# The hall of the demo's first floor — Room 101 of the building, though the demo's map has no plan of
# a room nested in the floor. The master and its PTZ slave work as one pair, so both stand in this
# hall, and the person they work on walks inside it.
ROOM101_SOUTH, ROOM101_WEST, ROOM101_NORTH, ROOM101_EAST = 38.9928704, -76.9379791, 38.9929603, -76.9378173
# The building's footprint in world metres (X east, Z south), in the direction the simulator stores
# it: Biomolecular Sciences as OpenStreetMap draws it, one corner per line. The demo's plan space must
# be this same outline, and the simulator's prism — one object of the world — the same shape.
MARYLAND_BUILDING_OUTLINE = (
    (-17.1, -81.3), (-17.0, -68.9), (-19.5, -68.8), (-19.4, -63.1), (-17.0, -63.2), (-17.0, -62.1),
    (-16.8, -50.0), (32.4, -50.3), (32.4, -62.8), (35.1, -63.0), (35.3, -63.8), (35.3, -69.0),
    (32.5, -69.0), (32.6, -72.9), (12.1, -72.9), (12.0, -77.0), (10.9, -77.0), (3.3, -76.9),
    (3.3, -81.4),
)
MARYLAND_BUILDING_HEIGHT_M = 11.78
# Every road of the world is this wide, so a building may come no closer than half of it.
MARYLAND_ROAD_WIDTH_M = 7.0
# The doorway in the building's north wall, in world metres: the only way between the hall and the
# parking lot the road pair watches. Mirrors MarylandWorld.cs's Door* constants.
MARYLAND_DOOR_X = -8.0
MARYLAND_DOOR_WIDTH = 1.8
MARYLAND_NORTH_WALL_Z = -81.35


@dataclass(frozen=True)
class DemoPlacement:
    """A demo camera's place: WGS84, height above the building floor, heading clockwise from north,
    and the horizontal field of view (for a PTZ camera, the wide end of its lens)."""
    id: str
    lat: float
    lon: float
    height: float
    heading: float
    hfov: float


DEMO_PLACEMENTS = [
    DemoPlacement("master", 38.9928120, -76.9378925, 2.8, 0, 45),
    DemoPlacement("ptz", 38.9928722, -76.9378196, 2.8, 310, 65),
    DemoPlacement("master2", 38.9933061, -76.9381467, 6.0, 125, 55),
    DemoPlacement("ptz2", 38.9931085, -76.9380312, 5.5, 88, 60),
]
DEMO_VEHICLE_CAMERA = "vehicle"
# The car that drives the ring road and carries the vehicle camera. Neither pair follows it: from
# where their masters stand it is too small in the picture for the detector.
DEMO_VEHICLE_ID = "v1"
# The detector's threshold, in normalized picture units: a figure shorter than this is not seen.
MIN_BOX_HEIGHT = 0.05
# What a check asks of a person's box: the detector drops anything under MIN_BOX_HEIGHT, so a route
# point has to clear it with a margin. A landmark a calibration stands a person on wants more, since
# the ground fit is made from that box.
SEEN_BOX_HEIGHT = 1.2 * MIN_BOX_HEIGHT
MARK_BOX_HEIGHT = 1.5 * MIN_BOX_HEIGHT

MARYLAND_HALL_PAIR = "hall"
MARYLAND_ROAD_PAIR = "road"
# How big the world marks a landmark: a block of the landmark's own colour, standing on its point.
# MarylandWorld.LandmarkSize, and half a metre — the yard's own size — so a person walking over one
# is still a person.
MARYLAND_LANDMARK_SIZE = 0.5

# The landmarks in the hall, in world metres (X east, Z south): in Room 101, around the square the
# person walks, where the master at (-8, -57) and the slave at (-1.7, -63.7) both see them, and spread
# across the master's picture so the ground fit is not extrapolated from one corner of it.
# The world marks each of them (MarylandWorld.Landmarks, `lm-hall-L1` …) — a landmark on the map
# and what a camera's picture shows are one point, which is what makes it findable while aiming.
MARYLAND_HALL_LANDMARKS = (
    Landmark("L1", -10.6, -71.6, "#e53935", "red"),
    Landmark("L2", -6.4, -71.6, "#fdd835", "yellow"),
    Landmark("L3", -10.6, -67.9, "#1e88e5", "blue"),
    Landmark("L4", -6.4, -67.9, "#f5f5f5", "white"),
    Landmark("L5", -8.5, -69.8, "#8e24aa", "purple"),
)

# The landmarks on the ground of the yard, in world metres: across the stretch the person walks out
# on, so the ground they walk over is the ground the pair is calibrated on, and across the yard
# master's picture rather than in one corner of it. They are not on one line: a ground homography
# needs points that span a plane. They stay inside the master's picture with a person's box big
# enough for its analytics, which is why they stop short of the walk's turn along the road. The
# farthest one also sets how far the slave's zoom samples reach: beyond it the slave keeps the
# zoom it learnt there rather than guess, so the marks follow the walk out as far as they can.
MARYLAND_ROAD_LANDMARKS = (
    Landmark("L1", -8.0, -84.0, "#e53935", "red"),
    Landmark("L2", -11.0, -91.0, "#fdd835", "yellow"),
    Landmark("L3", -4.0, -92.0, "#1e88e5", "blue"),
    Landmark("L4", -8.0, -98.0, "#f5f5f5", "white"),
    Landmark("L5", 2.0, -100.0, "#8e24aa", "purple"),
)

# The demo's calibration, as ta_vms's DemoWorlds pins it: the foot of a person standing on each
# landmark in the master's picture (x, y in shares of the frame), and the generic pan/tilt — and,
# for the hall's slave, the framing zoom — that centres it on the slave. Computed once from this
# world and checked against it below, so a pose or landmark that moves breaks the check instead of
# leaving the demo aiming at the old point.
MARYLAND_HALL_GROUND = {
    "L1": (0.288942, 0.606953), "L2": (0.630300, 0.606953), "L3": (0.219821, 0.740750),
    "L4": (0.672973, 0.740750), "L5": (0.454114, 0.662673),
}
MARYLAND_HALL_PTZ = {
    "L1": (0.008838, 0.395735, 0.208905), "L2": (0.106873, 0.328499, 0.133485),
    "L3": (-0.081842, 0.347742, 0.153152), "L4": (0.009885, 0.201190, 0.029643),
    "L5": (0.010502, 0.326694, 0.131707),
}
MARYLAND_ROAD_GROUND = {
    "L1": (0.786424, 0.514470), "L2": (0.714613, 0.582389), "L3": (0.542411, 0.526134),
    "L4": (0.458604, 0.603585), "L5": (0.256542, 0.523149),
}
# The yard's slave keeps the wide lens, so only its head is pinned.
MARYLAND_ROAD_PTZ = {
    "L1": (0.158695, 0.231184), "L2": (-0.024035, 0.067931), "L3": (-0.028428, 0.293989),
    "L4": (-0.175978, 0.256873), "L5": (-0.124647, 0.403267),
}

# The racks the simulator stands in Room 101: MarylandWorld's `rack-1` … `rack-4`, boxes along the
# room's west wall. ta_vms's floor diagram is marked at the middle of that row, so the picture on the
# plan stands where the cabinets do; check_demo compares the marker with these.
MARYLAND_RACK_X = -14.6
MARYLAND_RACK_Z = (-72.4, -69.9, -67.4, -64.9)


def demo_ground_marks(world, camera_id, landmarks):
    """The foot of a person standing on each landmark, in the master's picture: what ta_vms stores
    as ground marks, keyed by the landmark's name."""
    camera = demo_camera(world, camera_id, MASTER)
    return {landmark.name: project(camera, (landmark.x, 0.0, landmark.z)) for landmark in landmarks}


def demo_ptz_observations(world, camera_id, landmarks):
    """The generic pan/tilt that centre each landmark on a slave, and the framing zoom at its
    distance — what ta_vms stores as PTZ observations, keyed by the landmark's name."""
    camera = demo_camera(world, camera_id, SLAVE)
    limits = demo_limits(world, camera_id)
    result = {}
    for landmark in landmarks:
        target = (landmark.x, 0.0, landmark.z)
        pan, tilt = ptz_degrees(camera, target)
        _, _, distance = bearing(camera.position, target)
        result[landmark.name] = (*to_generic(pan, tilt, limits), framing_zoom(distance, limits))
    return result


def demo_camera(world, camera_id, base):
    """`base` at the Maryland world's own pose — the demo places its cameras from the same constants.
    Its lens comes from the world as well: the demo's pair does not always share the yard's."""
    camera = next(c for c in world["cameras"] if c["id"] == camera_id)
    position = camera["position"]
    hfov = camera["hFov"] if camera.get("kind") == "fixed" \
        else (camera.get("ptz") or {}).get("fovWide", base.hfov)
    return dataclasses.replace(
        base, x=position["x"], y=position["y"], z=position["z"], heading=camera["heading"],
        pitch=camera.get("pitch", base.pitch), hfov=hfov)


def demo_limits(world, camera_id):
    """The PTZ lens and axes a demo slave is configured with; the yard's when it has none."""
    camera = next(c for c in world["cameras"] if c["id"] == camera_id)
    ptz = camera.get("ptz")
    if not ptz:
        return SLAVE_LIMITS
    return PtzLimits(
        pan_min=ptz.get("panMin", SLAVE_LIMITS.pan_min), pan_max=ptz.get("panMax", SLAVE_LIMITS.pan_max),
        tilt_min=ptz.get("tiltMin", SLAVE_LIMITS.tilt_min), tilt_max=ptz.get("tiltMax", SLAVE_LIMITS.tilt_max),
        fov_wide=ptz.get("fovWide", SLAVE_LIMITS.fov_wide), fov_tele=ptz.get("fovTele", SLAVE_LIMITS.fov_tele),
        pan_reverse=ptz.get("panReverse", SLAVE_LIMITS.pan_reverse),
        tilt_reverse=ptz.get("tiltReverse", SLAVE_LIMITS.tilt_reverse))


def demo_pairs(world):
    """The demo's two pairs: the hall pair indoors and the yard pair outside its doorway.

    Both masters drive their slave with boxes of the walking person — the hall's indoors, the
    yard's once they are out on the ground — which is why both follow the `person` kind."""
    return [
        Pair(MARYLAND_HALL_PAIR,
             demo_camera(world, "master", MASTER),
             demo_camera(world, "ptz", SLAVE),
             limits=demo_limits(world, "ptz"),
             landmarks=MARYLAND_HALL_LANDMARKS, space="hall"),
        Pair(MARYLAND_ROAD_PAIR,
             demo_camera(world, "master2", MASTER2),
             demo_camera(world, "ptz2", SLAVE2),
             limits=demo_limits(world, "ptz2"),
             landmarks=MARYLAND_ROAD_LANDMARKS, target_kinds=("person",), space="road"),
    ]


def world_to_wgs84(world, position):
    """A world point in WGS84, by WorldGeo's equirectangular formula around the world's own anchor:
    the simulator's (x, y, z) metres, with north being the true bearing of world -Z."""
    geo = world.get("geo") or {}
    lat0 = geo.get("latitude", 0.0)
    lon0 = geo.get("longitude", 0.0)
    turn = math.radians(geo.get("north", 0.0))
    x, y, z = position["x"], position["y"], position["z"]
    east = x * math.cos(turn) - z * math.sin(turn)
    north = -x * math.sin(turn) - z * math.cos(turn)
    per_deg_lat = EARTH_RADIUS_M * math.pi / 180.0
    per_deg_lon = per_deg_lat * math.cos(math.radians(lat0))
    return lat0 + north / per_deg_lat, lon0 + east / per_deg_lon, geo.get("elevation", 0.0) + y


def _inside_room101(world, x, z):
    """Whether a world point is inside Room 101, where the demo's hall pair works."""
    lat, lon, _ = world_to_wgs84(world, {"x": x, "y": 0.0, "z": z})
    return ROOM101_SOUTH <= lat <= ROOM101_NORTH and ROOM101_WEST <= lon <= ROOM101_EAST


def ground_gap(lat1, lon1, lat2, lon2):
    """How far apart two WGS84 points are on the ground, metres."""
    per_deg_lat = EARTH_RADIUS_M * math.pi / 180.0
    per_deg_lon = per_deg_lat * math.cos(math.radians((lat1 + lat2) / 2.0))
    return math.hypot((lat1 - lat2) * per_deg_lat, (lon1 - lon2) * per_deg_lon)


def angle_gap(a, b):
    return abs((a - b + 180.0) % 360.0 - 180.0)


def _ring_of(obj):
    """A prism object's outline in the world's own metres: its points, which are local, moved to
    where the object stands."""
    position = obj.get("position") or {}
    return [(point["x"] + position.get("x", 0.0), point["z"] + position.get("z", 0.0))
            for point in obj.get("points") or []]


def _ground_segment_gap(a, b, p):
    dx, dz = b[0] - a[0], b[1] - a[1]
    length_squared = dx * dx + dz * dz
    t = 0.0 if length_squared <= 0 else max(0.0, min(1.0, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dz) / length_squared))
    return math.hypot(p[0] - (a[0] + dx * t), p[1] - (a[1] + dz * t))


def _distance_to_polyline(line, p):
    return min((_ground_segment_gap(a, b, p) for a, b in zip(line, line[1:])), default=float("inf"))


def _segments_cross(a0, a1, b0, b1):
    def cross(o, p, q):
        return (p[0] - o[0]) * (q[1] - o[1]) - (p[1] - o[1]) * (q[0] - o[0])

    d1, d2, d3, d4 = cross(b0, b1, a0), cross(b0, b1, a1), cross(a0, a1, b0), cross(a0, a1, b1)
    return ((d1 > 0) != (d2 > 0)) and ((d3 > 0) != (d4 > 0))


def _ring_to_road_gap(ring, line):
    """How far a footprint's outline is from a road's centre line: the road's own width has to fit in
    the gap, or the building stands on the road."""
    best = min((_distance_to_polyline(line, p) for p in ring), default=float("inf"))
    for a, b in zip(ring, ring[1:] + ring[:1]):
        for c, d in zip(line, line[1:]):
            if _segments_cross(a, b, c, d):
                return 0.0
            best = min(best, _ground_segment_gap(a, b, c))
    return best


def _spread_ratio(points):
    """How far a set of points spans a plane rather than a line: the smaller principal spread
    against the larger one, as GroundHomography's fit measures it (it refuses below 0.05)."""
    mx = sum(p[0] for p in points) / len(points)
    my = sum(p[1] for p in points) / len(points)
    sxx = syy = sxy = 0.0
    for x, y in points:
        sxx += (x - mx) ** 2
        syy += (y - my) ** 2
        sxy += (x - mx) * (y - my)
    mean = (sxx + syy) / 2
    root = math.sqrt(((sxx - syy) / 2) ** 2 + sxy * sxy)
    major, minor = mean + root, max(0.0, mean - root)
    return math.sqrt(minor / major) if major > 0 else 0.0


def check_maryland(world):
    """Raises AssertionError if the world does not stand where ta_vms's demo placements put it."""
    geo = world.get("geo") or {}
    assert ground_gap(geo.get("latitude", 0.0), geo.get("longitude", 0.0), MARYLAND_LAT0, MARYLAND_LON0) \
        <= MARYLAND_POSITION_TOLERANCE_M, f"the world is anchored at {geo}, not at the demo building"
    assert geo.get("north", 0.0) == 0, f"the world's north is {geo.get('north')}, not 0"

    cameras = {camera["id"]: camera for camera in world.get("cameras", [])}
    for placement in DEMO_PLACEMENTS:
        camera = cameras.get(placement.id)
        assert camera is not None, f"the world has no '{placement.id}' camera"
        lat, lon, elevation = world_to_wgs84(world, camera["position"])
        gap = ground_gap(lat, lon, placement.lat, placement.lon)
        assert gap <= MARYLAND_POSITION_TOLERANCE_M, \
            f"{placement.id} stands {gap:.2f} m from its demo placement ({lat:.7f}, {lon:.7f})"
        assert abs(elevation - placement.height) <= MARYLAND_POSITION_TOLERANCE_M, \
            f"{placement.id} is {elevation:.2f} m up, the demo places it at {placement.height} m"
        assert angle_gap(camera["heading"], placement.heading) <= MARYLAND_ANGLE_TOLERANCE_DEG, \
            f"{placement.id} faces {camera['heading']}, the demo {placement.heading}"
        hfov = camera["hFov"] if camera.get("kind") == "fixed" else (camera.get("ptz") or {}).get("fovWide")
        assert hfov is not None and abs(hfov - placement.hfov) <= MARYLAND_ANGLE_TOLERANCE_DEG, \
            f"{placement.id} is {hfov} deg wide, the demo {placement.hfov}"

    # The building is the real one: one prism of Biomolecular Sciences' own footprint, at the height
    # the map gives it. The demo's plan space is this same outline, so the map and the world agree
    # corner for corner rather than merely overlapping.
    building = next((obj for obj in world.get("objects", []) if obj.get("id") == "building"), None)
    assert building is not None, "the world has no 'building' object"
    assert building.get("type") == "prism", f"the building is a '{building.get('type')}', not a prism"
    assert abs((building.get("scale") or {}).get("y", 0.0) - MARYLAND_BUILDING_HEIGHT_M) <= 0.01, \
        f"the building is {(building.get('scale') or {}).get('y')} m tall, not {MARYLAND_BUILDING_HEIGHT_M}"
    ring = _ring_of(building)
    assert len(ring) == len(MARYLAND_BUILDING_OUTLINE), \
        f"the building has {len(ring)} corners, not {len(MARYLAND_BUILDING_OUTLINE)}"
    for x, z in MARYLAND_BUILDING_OUTLINE:
        assert any(math.hypot(x - px, z - pz) <= 0.01 for px, pz in ring), \
            f"the building has no corner at ({x}, {z})"

    # The racks of the demo's floor diagram: boxes along Room 101's west wall, at the points the
    # map's diagram marker stands in the middle of.
    objects = {obj.get("id"): obj for obj in world.get("objects", [])}
    for index, z in enumerate(MARYLAND_RACK_Z, start=1):
        rack = objects.get(f"rack-{index}")
        assert rack is not None, f"the world has no 'rack-{index}'"
        assert rack.get("type") == "box", f"rack-{index} is a '{rack.get('type')}', not a box"
        position = rack.get("position") or {}
        assert abs(position.get("x", 0.0) - MARYLAND_RACK_X) <= 0.01, \
            f"rack-{index} stands at x {position.get('x')}, not {MARYLAND_RACK_X}"
        assert abs(position.get("z", 0.0) - z) <= 0.01, \
            f"rack-{index} stands at z {position.get('z')}, not {z}"

    # The calibration ta_vms ships with the demo must be this world's own projection: the feet of a
    # person on each landmark in the masters' pictures, and the head positions that centre them on
    # the slaves. A pose or a landmark that moves fails here rather than leaving the demo aiming at
    # the old point.
    for master_id, slave_id, landmarks, ground, ptz, pinned_zoom in (
        ("master", "ptz", MARYLAND_HALL_LANDMARKS, MARYLAND_HALL_GROUND, MARYLAND_HALL_PTZ, True),
        ("master2", "ptz2", MARYLAND_ROAD_LANDMARKS, MARYLAND_ROAD_GROUND, MARYLAND_ROAD_PTZ, False),
    ):
        for name, (x, y) in demo_ground_marks(world, master_id, landmarks).items():
            ex, ey = ground[name]
            assert abs(x - ex) <= 1e-4 and abs(y - ey) <= 1e-4, \
                f"{master_id}'s ground mark at {name} is ({x:.6f}, {y:.6f}), the demo pins ({ex}, {ey})"
        for name, (pan, tilt, zoom) in demo_ptz_observations(world, slave_id, landmarks).items():
            row = ptz[name]
            assert abs(pan - row[0]) <= 1e-4 and abs(tilt - row[1]) <= 1e-4, \
                f"{slave_id}'s observation of {name} is ({pan:.6f}, {tilt:.6f}), the demo pins {row[:2]}"
            if pinned_zoom:
                assert abs(zoom - row[2]) <= 1e-4, \
                    f"{slave_id}'s framing zoom at {name} is {zoom:.6f}, the demo pins {row[2]}"

    # No building of this world stands on a road: the defect this milestone was opened for was a hall
    # laid over two real streets.
    roads = world.get("roads", [])
    assert roads, "the world has no roads"
    for obj in world.get("objects", []):
        if obj.get("type") != "prism" or (obj.get("scale") or {}).get("y", 0.0) < 1:
            continue
        for road in roads:
            line = [(point["x"], point["z"]) for point in road.get("points", [])]
            gap = _ring_to_road_gap(_ring_of(obj), line)
            half = road.get("width", MARYLAND_ROAD_WIDTH_M) / 2
            assert gap >= half, f"{obj.get('id')} is {gap:.1f} m from {road.get('id')}, whose half width is {half:.1f}"

    # Each pair works as one: the master detects, the slave aims where its boxes say. Every slave
    # carries its head in the metadata track, and every master boxes what it sees.
    for name in ("master", "master2"):
        assert (cameras[name].get("metadata") or {}).get("analytics") is True, f"{name} has no analytics"
    for name in ("ptz", "ptz2"):
        assert (cameras[name].get("metadata") or {}).get("ptzStatus") is True, \
            f"{name} does not report its head in the telemetry"
    walkers = world.get("walkers", [])
    assert walkers, "the world has no person for the pair to work on"
    assert all(w.get("detectable", True) for w in walkers), "the walker is not detectable"
    # The slave stands in the room its master watches, so the person it is aimed at is the person
    # its own picture holds; the master stands outside the room, on the line out of it, because one
    # lens has to hold both the room and the doorway at the far end of the hall.
    ptz = next(placement for placement in DEMO_PLACEMENTS if placement.id == "ptz")
    assert ROOM101_SOUTH <= ptz.lat <= ROOM101_NORTH and ROOM101_WEST <= ptz.lon <= ROOM101_EAST, \
        "ptz does not stand in the hall with the person it works on"
    # The person walks the hall and Room 101, where the hall pair sees them, and out of the doorway
    # in the north wall, where the yard pair takes over: both halves of the demo have someone to
    # follow. The doorway is the only way between them, so every crossing of the wall line has to
    # run through its gap.
    hall = demo_camera(world, "master", MASTER)
    hall_pose = level_pose(hall)
    road_pose = level_pose(demo_pairs(world)[1].master)
    door_west = MARYLAND_DOOR_X - MARYLAND_DOOR_WIDTH / 2
    door_east = MARYLAND_DOOR_X + MARYLAND_DOOR_WIDTH / 2
    for walker in walkers:
        route = [(point["x"], point["z"]) for point in walker.get("route", [])]
        assert len(route) >= 2, "the person has nowhere to walk"
        path = ([*route, route[0]] if walker.get("return", "loop") == "loop"
                else [*route, *reversed(route[:-1])])
        room = [
            (x, z) for x, z in route
            if _inside_room101(world, x, z)
        ]
        # Inside the building, where the hall pair works; and outside it, not merely out of Room
        # 101, where the yard pair does: the route has to cross the north wall.
        inside_building = [(x, z) for x, z in route if z > MARYLAND_NORTH_WALL_Z]
        outside = [(x, z) for x, z in route if z < MARYLAND_NORTH_WALL_Z]
        assert room, "the person never walks inside Room 101, where the hall slave stands"
        assert inside_building, "the person never walks inside the building"
        assert outside, "the person never leaves the building, so the yard pair has nobody outside"
        # Each master holds the whole of its own half of the walk, not a corner of it: the hall
        # master from the room to the doorway, the yard master from the doorway to the road and
        # back. The slave is aimed by those boxes, so a point either master misses is a point the
        # slave stops following.
        for x, z in inside_building:
            inside, span = person_in_frame(hall_pose, (x, 0.0, z))
            assert inside and span >= SEEN_BOX_HEIGHT, \
                f"the hall master does not see the person at ({x}, {z}): in frame {inside}, span {span:.3f}"
        for x, z in outside:
            inside, span = person_in_frame(road_pose, (x, 0.0, z))
            assert inside and span >= SEEN_BOX_HEIGHT, \
                f"the yard master does not see the person at ({x}, {z}): in frame {inside}, span {span:.3f}"
        for (ax, az), (bx, bz) in zip(path, path[1:]):
            from_side, to_side = az - MARYLAND_NORTH_WALL_Z, bz - MARYLAND_NORTH_WALL_Z
            if from_side == 0 or to_side == 0 or (from_side > 0) == (to_side > 0):
                continue
            t = (MARYLAND_NORTH_WALL_Z - az) / (bz - az)
            crossing = ax + (bx - ax) * t
            assert door_west <= crossing <= door_east, \
                f"the person walks through the north wall at x = {crossing:.2f}, outside the doorway"

    vehicle = cameras.get(DEMO_VEHICLE_CAMERA)
    assert vehicle is not None, f"the world has no '{DEMO_VEHICLE_CAMERA}' camera"
    vehicle_id = vehicle.get("mountedOnVehicleId")
    assert vehicle_id and any(v["id"] == vehicle_id for v in world.get("vehicles", [])), \
        f"{DEMO_VEHICLE_CAMERA} does not ride a vehicle of the world"

    # The landmarks of both pairs. They stand on the ground each master watches, so the master must
    # hold them well inside its picture with a person on them big enough for its analytics; the
    # marks must span a plane (a ground homography refuses five points on one line); and each slave
    # must be able to turn onto every one of them within its own head's limits, with enough spread
    # in pan and tilt for the calibration to tell its axes apart.
    for pair in demo_pairs(world):
        marks = list(pair.landmarks)
        assert _spread_ratio([(lm.x, lm.z) for lm in marks]) >= 0.05, \
            f"the {pair.name} landmarks lie almost on one line; a ground homography cannot be fitted"
        for lm in marks:
            uv = project(pair.master, (lm.x, 0.0, lm.z))
            assert uv and 0.05 < uv[0] < 0.95 and 0.05 < uv[1] < 0.95, \
                f"{lm.name} is not well inside {pair.master.id}'s picture: {uv}"
            foot = project(pair.master, (lm.x, 0.0, lm.z))
            head = project(pair.master, (lm.x, WALKER_HEIGHT, lm.z))
            assert head and foot and foot[1] - head[1] >= MARK_BOX_HEIGHT, \
                f"a person on {lm.name} is too small for {pair.master.id}'s analytics"
            pan, tilt = ptz_degrees(pair.slave, (lm.x, 0.0, lm.z))
            assert pair.limits.pan_min <= pan <= pair.limits.pan_max \
                and pair.limits.tilt_min <= tilt <= pair.limits.tilt_max, \
                f"{lm.name} is beyond {pair.slave.id}'s head: pan {pan:.1f}, tilt {tilt:.1f}"
        pans = [ptz_degrees(pair.slave, (lm.x, 0.0, lm.z)) for lm in marks]
        assert max(p for p, _ in pans) - min(p for p, _ in pans) >= 5, \
            f"{pair.slave.id}: the {pair.name} landmarks span only " \
            f"{max(p for p, _ in pans) - min(p for p, _ in pans):.1f} deg of pan"
        assert max(t for _, t in pans) - min(t for _, t in pans) >= 1, \
            f"{pair.slave.id}: the {pair.name} landmarks span only " \
            f"{max(t for _, t in pans) - min(t for _, t in pans):.1f} deg of tilt"
    for name in ("ptz", "ptz2"):
        assert not (cameras[name].get("metadata") or {}).get("analytics"), \
            f"{name} boxes for itself; its master's boxes are what aims it"

    # The markers the world stands on the landmarks: one block per point, of the landmark's own
    # colour, on the ground the calibration aims at. Without them a landmark on the demo's map has
    # nothing to find in a camera's picture — the demo is a laboratory, and the operator aiming or
    # calibrating a camera has to see what they are aiming at.
    objects = {obj.get("id"): obj for obj in world.get("objects", [])}
    for pair in demo_pairs(world):
        for lm in pair.landmarks:
            marker_id = f"lm-{pair.name}-{lm.name.lower()}"
            marker = objects.get(marker_id)
            assert marker is not None, f"the world has no '{marker_id}' marker for {lm.label}"
            assert marker.get("type") == "box", f"{marker_id} is a '{marker.get('type')}', not a box"
            position = marker.get("position") or {}
            scale = marker.get("scale") or {}
            assert math.hypot(position.get("x", 0) - lm.x, position.get("z", 0) - lm.z) <= 0.01, \
                f"{marker_id} stands at ({position.get('x')}, {position.get('z')}), not at ({lm.x}, {lm.z})"
            assert abs(position.get("y", 0)) <= 0.01, f"{marker_id} does not stand on the ground"
            assert (marker.get("color") or "").lower() == lm.color.lower(), \
                f"{marker_id} is {marker.get('color')}, not {lm.color}"
            assert all(abs(scale.get(axis, 0) - MARYLAND_LANDMARK_SIZE) <= 0.01 for axis in "xyz"), \
                f"{marker_id} is {scale}, not a {MARYLAND_LANDMARK_SIZE} m block"

    # The car still drives the ring road and still carries its own camera; it is ambience now, not
    # what either pair follows.
    car = next((v for v in world.get("vehicles", []) if v["id"] == DEMO_VEHICLE_ID), None)
    assert car is not None, f"the world has no '{DEMO_VEHICLE_ID}' car"
    assert car.get("detectable", True), f"{DEMO_VEHICLE_ID} is not detectable"
    route = car.get("route", [])
    assert len(route) >= 20, f"{DEMO_VEHICLE_ID} has no ring to drive, only {len(route)} points"
    assert car.get("return") == "loop", f"{DEMO_VEHICLE_ID} does not come round the block again"

    # The master's three relays show every kind of link a relay can have: one lights a lamp, one
    # drives the slave camera's sensor, and one drives a sensor of the master itself. A lamp is an
    # actuator of the world, not a part of a camera.
    master = cameras["master"]
    relays = {relay["token"] for relay in master.get("relayOutputs", [])}
    assert {"lamp", "peer", "loop"} <= relays, f"master has no relays to wire, only {relays}"
    assert any(device["token"] == "input" for device in master.get("digitalInputs", [])), \
        "master has no 'input' sensor"
    ptz = cameras["ptz"]
    assert any(device["token"] == "input" for device in ptz.get("digitalInputs", [])), \
        "ptz has no 'input' sensor"
    assert any(lamp["id"] == "alert" for lamp in world.get("lamps", [])), "the world has no 'alert' lamp"
    wires = world.get("wires", [])

    def wired(relay_token, **sink):
        return any(wire.get("relayCameraId") == "master" and wire.get("relayToken") == relay_token
                   and all(wire.get(key) == value for key, value in sink.items())
                   for wire in wires)

    assert wired("lamp", lampId="alert"), "no wire runs from master's 'lamp' relay to the 'alert' lamp"
    assert wired("peer", inputCameraId="ptz", inputToken="input"), \
        "no wire runs from master's 'peer' relay to ptz's sensor"
    assert wired("loop", inputCameraId="master", inputToken="input"), \
        "no wire runs from master's 'loop' relay to the master's own sensor"
    # The car carries no device: a wire to it would move with it, and every camera that saw the wire
    # would redraw at every frame of the drive.
    assert not vehicle.get("relayOutputs") and not vehicle.get("digitalInputs"), \
        f"{DEMO_VEHICLE_CAMERA} should carry no devices"


def load_maryland(path, ta_install_world=None):
    """The demo world OnvifSim ships, checked against the demo placements; ta_install_world, when
    given, is the same file as a deployment's demo profile seeds, and must be identical."""
    path = Path(path)
    world = json.loads(path.read_text())
    check_maryland(world)
    if ta_install_world is not None:
        copy = Path(ta_install_world)
        if copy.exists():
            assert copy.read_text().replace("\r\n", "\n") == path.read_text().replace("\r\n", "\n"), \
                f"{copy} differs from {path}: the demo profile would seed another world"
    return world


# ── Self-check: the world is one the checks can work in ─────────────────────────


def _distance_to_segment(px, pz, ax, az, bx, bz):
    dx, dz = bx - ax, bz - az
    t = max(0.0, min(1.0, ((px - ax) * dx + (pz - az) * dz) / (dx * dx + dz * dz)))
    return math.hypot(px - (ax + t * dx), pz - (az + t * dz))


def _route_samples(step=1.0):
    loop = [*WALKER_ROUTE, WALKER_ROUTE[0]]
    for (ax, az), (bx, bz) in zip(loop, loop[1:]):
        n = max(1, int(math.hypot(bx - ax, bz - az) / step))
        for i in range(n):
            yield ax + (bx - ax) * i / n, az + (bz - az) * i / n


def check():
    """Raises AssertionError if the world would make calibration or the run meaningless."""
    # No scenery stands between a camera and anything it has to see: a landmark, or the person
    # anywhere on their loop. Checked on the ground plan, ignoring height, so it errs on the safe side.
    targets = [(lm.name, lm.x, lm.z) for lm in LANDMARKS] + [("route", x, z) for x, z in _route_samples()]
    for s in SCENERY:
        for camera in STREAMING_CAMERAS:
            for name, x, z in targets:
                gap = _distance_to_segment(s.x, s.z, camera.x, camera.z, x, z)
                assert gap > s.radius, f"{s.id} stands {gap:.1f} m from {camera.id}'s line to {name} ({x:.0f},{z:.0f})"
        for name, x, z in targets:
            assert math.hypot(s.x - x, s.z - z) > s.radius + 1, f"{s.id} stands on {name}"

    for pair in PAIRS:
        _check_pair(pair)

    # Everything a pair stands on or looks at is on the plan picture, clear of its edges.
    half_h = PLAN_HALF_WIDTH * PLAN_H / PLAN_W
    for name, x, z in ([(lm.name, lm.x, lm.z) for lm in LANDMARKS] + [("route", x, z) for x, z in WALKER_ROUTE]
                       + [(c.id, c.x, c.z) for c in STREAMING_CAMERAS]):
        assert abs(x - PLAN_CENTRE[0]) < PLAN_HALF_WIDTH - 2 and abs(z - PLAN_CENTRE[1]) < half_h - 2, \
            f"{name} at ({x},{z}) is off the plan picture"
    assert math.hypot(PLAN_ALTITUDE, PLAN_HALF_WIDTH, half_h) < 150, "the plan picture's corners reach into the fog"


def _check_pair(pair):
    master, slave = pair.master, pair.slave
    # The master sees every landmark and every corner of the route, clear of the frame edges.
    for name, x, z in [(lm.name, lm.x, lm.z) for lm in LANDMARKS] + [("route", x, z) for x, z in WALKER_ROUTE]:
        uv = project(master, (x, 0.0, z))
        assert uv and 0.05 < uv[0] < 0.95 and 0.05 < uv[1] < 0.95, \
            f"{name} at ({x},{z}) is not well inside {master.id}'s picture: {uv}"
        top = project(master, (x, WALKER_HEIGHT, z))
        # OnvifSim's minimum box is 0.05 in -1..1 units, i.e. 0.025 of the frame height.
        assert uv[1] - top[1] > 0.04, f"a person at {name} would be too small for {master.id}'s analytics"

    # The slave's landmarks spread far enough for the geo fit, and keep away from its pan seam: the
    # level bench is the one without it, and the `seam` mount puts it among them on purpose.
    readings = [ptz_degrees(slave, (lm.x, 0.0, lm.z)) for lm in LANDMARKS]
    pans = [p for p, _ in readings]
    tilts = [t for _, t in readings]
    assert max(pans) - min(pans) >= 45, f"{slave.id}: landmark pans span only {max(pans) - min(pans):.1f} deg"
    assert max(tilts) - min(tilts) >= 1, f"{slave.id}: landmark tilts span only {max(tilts) - min(tilts):.1f} deg"
    assert all(abs(p) < 150 for p in pans), f"a landmark lies near {slave.id}'s pan seam"
    for x, z in WALKER_ROUTE:
        assert abs(ptz_degrees(slave, (x, 0.0, z))[0]) < 150, f"the route crosses {slave.id}'s pan seam"


def summary(lat0=DEFAULT_LAT0, lon0=DEFAULT_LON0):
    lines = []
    for pair in PAIRS:
        # The map pair lives in the real anchor's WGS84, the plan pair in made-up WGS84 around (0, 0).
        a_lat, a_lon = (lat0, lon0) if pair is MAP_PAIR else (0.0, 0.0)
        lines += [f"{pair.name}: {pair.master.id} and {pair.slave.id}, anchored at ({a_lat}, {a_lon})",
                  "landmark          x      z      lat          lon          slave pan  tilt   generic x  y      master u  v"]
        for lm in LANDMARKS:
            lat, lon = to_wgs84(lm.x, lm.z, a_lat, a_lon)
            pan, tilt = ptz_degrees(pair.slave, (lm.x, 0.0, lm.z))
            gx, gy = to_generic(pan, tilt, pair.limits)
            u, v = project(pair.master, (lm.x, 0.0, lm.z))
            lines.append(
                f"{lm.label:<15} {lm.x:6.1f} {lm.z:6.1f}  {lat:11.7f}  {lon:11.7f}  {pan:8.2f} {tilt:6.2f}  {gx:8.4f} {gy:7.4f}  {u:7.3f} {v:6.3f}")
        lines.append("")
    corners = plan_corners()
    lines.append(f"plan picture {PLAN_W}x{PLAN_H}, camera {TOPDOWN.hfov:.3f} deg wide at {PLAN_ALTITUDE:.0f} m; corners "
                 + ", ".join(f"{k} ({lat:.7f}, {lon:.7f})" for k, (lat, lon) in corners.items()))
    return "\n".join(lines)


if __name__ == "__main__":
    if "--check-maryland" in sys.argv:
        # The two checkouts beside this one: the world OnvifSim ships, and the copy a deployment's
        # demo profile seeds. Both must stand where DemoWorlds places the cameras.
        world = load_maryland(
            REPO.parent / "OnvifSim" / "data" / "worlds" / "maryland.json",
            REPO.parent / "ta_install" / "init_files" / "onvifsim" / "worlds" / "maryland.json")
        geo = world["geo"]
        print(f"Maryland OK: {len(world['cameras'])} cameras at the demo placements, "
              f"anchored {geo['latitude']}, {geo['longitude']}")
    else:
        check()
        if "--dump" in sys.argv:
            print(json.dumps(build_world(8101), indent=2))
        else:
            print(summary())
