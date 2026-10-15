"""The small test world: one fixed master, one PTZ slave, five landmarks and one walking person,
with some scenery around them to find one's way by.

Everything the harness knows about the world comes from here, so the world OnvifSim renders and
the geometry the checks compare against cannot drift apart.

OnvifSim's coordinates are metres with X east, Y up and Z south (north is -Z); in ENU that is
E = X, N = -Z, U = Y. The terrain is flat at zero everywhere, so every height is absolute.

Run it directly to print the world JSON and the expected numbers:

    python3 tests/onvifsim/world.py            # summary
    python3 tests/onvifsim/world.py --dump     # the world JSON OnvifSim is given
"""

import base64
import math
import struct
import sys
from dataclasses import dataclass

# WGS84 anchor of the sim's origin. Not on the equator, so a cos(latitude) left out anywhere shows
# up as an east-west error instead of cancelling out.
DEFAULT_LAT0 = 41.3
DEFAULT_LON0 = 69.24

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
    pitch: float  # degrees, up positive
    hfov: float = 70.0  # fixed cameras; a PTZ camera's comes from its zoom
    # Whether the camera boxes people in its metadata track. Only the master's boxes are used;
    # the slave's would be drawn over its video and mean nothing to the aiming.
    analytics: bool = True

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


MASTER = Camera("master", "Master (fixed)", "fixed", 0.0, 6.0, 20.0, heading=0.0, pitch=-18.0, hfov=70.0)
SLAVE = Camera("ptz", "Slave (PTZ)", "ptz", 28.0, 9.0, -14.0, heading=240.0, pitch=0.0, analytics=False)
SLAVE_LIMITS = PtzLimits()

VIDEO_W, VIDEO_H = 640, 360

# Not green: the grass is.
LANDMARKS = [
    Landmark("L1", -9.0, 4.0, "#e53935", "red"),
    Landmark("L2", 9.0, 4.0, "#fdd835", "yellow"),
    Landmark("L3", -13.0, -12.0, "#1e88e5", "blue"),
    Landmark("L4", 13.0, -12.0, "#f5f5f5", "white"),
    Landmark("L5", 0.0, -24.0, "#8e24aa", "purple"),
]
LANDMARK_SIZE = 0.5  # metres; small enough that a person standing on one is still seen

WALKER_ID = "w1"
WALKER_ROUTE = [(-8.0, 0.0), (8.0, 0.0), (8.0, -16.0), (-8.0, -16.0)]
WALKER_SPEED = 1.0
WALKER_HEIGHT = 1.75


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
    """Pan and tilt (degrees, relative to the mount) that centre target: OnvifSim's head is exact."""
    az, el, _ = bearing(camera.position, target)
    return wrap180(az - camera.heading), el - camera.pitch


def to_generic(pan, tilt, limits=SLAVE_LIMITS):
    """OnvifSim's ONVIF generic position for a head in degrees (CameraPtz.ToGeneric)."""
    span = 360.0 if limits.pan_max - limits.pan_min >= 360.0 else limits.pan_max - limits.pan_min
    x = (pan - limits.pan_min) / span * 2.0 - 1.0
    y = (tilt - limits.tilt_min) / (limits.tilt_max - limits.tilt_min) * 2.0 - 1.0
    return x, y


def project(camera, point, hfov=None, width=VIDEO_W, height=VIDEO_H):
    """Where a sim point lands in a camera's picture, 0..1 from the top-left; None behind it."""
    fwd = direction(camera.heading, camera.pitch)
    h = math.radians(camera.heading)
    right = (math.cos(h), 0.0, math.sin(h))
    up = (
        right[1] * fwd[2] - right[2] * fwd[1],
        right[2] * fwd[0] - right[0] * fwd[2],
        right[0] * fwd[1] - right[1] * fwd[0],
    )
    d = tuple(p - c for p, c in zip(point, camera.position))
    depth = sum(a * b for a, b in zip(d, fwd))
    if depth <= 0:
        return None
    tan_h = math.tan(math.radians((hfov or camera.hfov) / 2.0))
    tan_v = tan_h / (width / height)
    x_ndc = sum(a * b for a, b in zip(d, right)) / depth / tan_h
    y_ndc = sum(a * b for a, b in zip(d, up)) / depth / tan_v
    return (x_ndc + 1.0) / 2.0, (1.0 - y_ndc) / 2.0


def in_middle_third(pose, point):
    """Whether a point is in the middle third of a camera's picture, both ways, for a pose as
    OnvifSim's /api/cameras/{id}/pose reports it (heading and pitch include the PTZ head)."""
    p = pose["position"]
    camera = Camera("pose", "", "fixed", p["x"], p["y"], p["z"], pose["heading"], pose["pitch"], pose["hfov"])
    uv = project(camera, point)
    return uv is not None and 1 / 3 < uv[0] < 2 / 3 and 1 / 3 < uv[1] < 2 / 3


# ── The world OnvifSim is given ─────────────────────────────────────────────────


def _flat_terrain():
    n = (TERRAIN_RESOLUTION + 1) ** 2
    heights = base64.b64encode(struct.pack(f"<{n}f", *([0.0] * n))).decode()
    return {"size": TERRAIN_SIZE, "resolution": TERRAIN_RESOLUTION, "heights": heights}


def _camera_json(camera, onvif_port):
    video = {"width": VIDEO_W, "height": VIDEO_H, "fps": 10, "quality": 70}
    body = {
        "id": camera.id,
        "name": camera.name,
        "kind": camera.kind,
        "position": {"x": camera.x, "y": camera.y, "z": camera.z},
        "heading": camera.heading,
        "pitch": camera.pitch,
        "roll": 0,
        "hFov": camera.hfov,
        "video": video,
        "h264": {"width": VIDEO_W, "height": VIDEO_H, "fps": 10, "bitrateKbps": 1024, "govLength": 10},
        "metadata": {"analytics": camera.analytics, "ptzStatus": False},
        "onvifPort": onvif_port,
        "users": USERS,
    }
    if camera.kind == "ptz":
        lim = SLAVE_LIMITS
        body["ptz"] = {
            "panMin": lim.pan_min, "panMax": lim.pan_max, "tiltMin": lim.tilt_min, "tiltMax": lim.tilt_max,
            "fovWide": lim.fov_wide, "fovTele": lim.fov_tele,
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


def build_world(onvif_base_port, with_walker=True):
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
        "terrain": _flat_terrain(),
        "objects": landmarks + scenery,
        "roads": roads,
        "walkers": walkers,
        "cameras": [_camera_json(MASTER, onvif_base_port), _camera_json(SLAVE, onvif_base_port + 1)],
    }


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
        for camera in (MASTER, SLAVE):
            for name, x, z in targets:
                gap = _distance_to_segment(s.x, s.z, camera.x, camera.z, x, z)
                assert gap > s.radius, f"{s.id} stands {gap:.1f} m from {camera.id}'s line to {name} ({x:.0f},{z:.0f})"
        for name, x, z in targets:
            assert math.hypot(s.x - x, s.z - z) > s.radius + 1, f"{s.id} stands on {name}"

    # The master sees every landmark and every corner of the route, clear of the frame edges.
    for name, x, z in [(lm.name, lm.x, lm.z) for lm in LANDMARKS] + [("route", x, z) for x, z in WALKER_ROUTE]:
        uv = project(MASTER, (x, 0.0, z))
        assert uv and 0.05 < uv[0] < 0.95 and 0.05 < uv[1] < 0.95, f"{name} at ({x},{z}) is not well inside the master's picture: {uv}"
        top = project(MASTER, (x, WALKER_HEIGHT, z))
        # OnvifSim's minimum box is 0.05 in -1..1 units, i.e. 0.025 of the frame height.
        assert uv[1] - top[1] > 0.04, f"a person at {name} would be too small for the master's analytics"

    # The slave's landmarks spread far enough for the geo fit, and keep away from its pan seam.
    readings = [ptz_degrees(SLAVE, (lm.x, 0.0, lm.z)) for lm in LANDMARKS]
    pans = [p for p, _ in readings]
    tilts = [t for _, t in readings]
    assert max(pans) - min(pans) >= 45, f"landmark pans span only {max(pans) - min(pans):.1f} deg"
    assert max(tilts) - min(tilts) >= 1, f"landmark tilts span only {max(tilts) - min(tilts):.1f} deg"
    assert all(abs(p) < 150 for p in pans), "a landmark lies near the slave's pan seam"
    for x, z in WALKER_ROUTE:
        assert abs(ptz_degrees(SLAVE, (x, 0.0, z))[0]) < 150, "the route crosses the slave's pan seam"


def summary(lat0=DEFAULT_LAT0, lon0=DEFAULT_LON0):
    lines = ["landmark          x      z      lat          lon          slave pan  tilt   generic x  y      master u  v"]
    for lm in LANDMARKS:
        lat, lon = to_wgs84(lm.x, lm.z, lat0, lon0)
        pan, tilt = ptz_degrees(SLAVE, (lm.x, 0.0, lm.z))
        gx, gy = to_generic(pan, tilt)
        u, v = project(MASTER, (lm.x, 0.0, lm.z))
        lines.append(
            f"{lm.label:<15} {lm.x:6.1f} {lm.z:6.1f}  {lat:.7f}  {lon:.7f}  {pan:8.2f} {tilt:6.2f}  {gx:8.4f} {gy:7.4f}  {u:7.3f} {v:6.3f}")
    return "\n".join(lines)


if __name__ == "__main__":
    import json

    check()
    if "--dump" in sys.argv:
        print(json.dumps(build_world(8101), indent=2))
    else:
        print(summary())
