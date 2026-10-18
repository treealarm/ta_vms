"""OnvifSim's HTTP API: the world, the PTZ heads and the ground truth."""

import time

from labhttp import fetch_bytes, request, sse


class Sim:
    def __init__(self, base_url):
        self.base = base_url.rstrip("/")

    def _url(self, path):
        return f"{self.base}{path}"

    def ready(self):
        try:
            request("GET", self._url("/api/cameras"), timeout=2)
            return True
        except Exception:
            return False

    def put_world(self, world):
        request("PUT", self._url("/api/world"), world, timeout=30)

    def status(self, seconds=5.0):
        """The `status` event: RTSP port, whether H.264 is on offer, ONVIF port per camera."""
        for event, data in sse(self._url("/api/events"), seconds=seconds):
            if event == "status":
                return data
        raise RuntimeError("OnvifSim sent no status event")

    def put_camera(self, camera):
        """Adds a camera or replaces it, keeping where its PTZ head points (OnvifSim keeps that)."""
        request("PUT", self._url(f"/api/cameras/{camera['id']}"), camera)

    def put_walker(self, walker_id, walker):
        request("PUT", self._url(f"/api/walkers/{walker_id}"), walker)

    def delete_walker(self, walker_id):
        try:
            request("DELETE", self._url(f"/api/walkers/{walker_id}"))
        except Exception as e:  # already gone is fine
            if getattr(e, "status", None) != 404:
                raise

    def goto(self, camera_id, pan, tilt, zoom=0.0):
        """Drives the head in degrees, as a person would with the editor's sliders."""
        request("POST", self._url(f"/api/cameras/{camera_id}/ptz/goto"), {"pan": pan, "tilt": tilt, "zoom": zoom})

    def aim(self, camera_id, point):
        """Turns the head until a sim point is in the middle of its picture, however the camera hangs,
        as a person with a joystick puts a landmark in the crosshair; returns (pan, tilt) in degrees."""
        x, y, z = point
        answer = request("POST", self._url(f"/api/cameras/{camera_id}/ptz/aim"), {"x": x, "y": y, "z": z})
        return answer["pan"], answer["tilt"]

    def snapshot(self, camera_id):
        """A JPEG of what the camera sees now, rendered on request."""
        return fetch_bytes(self._url(f"/api/cameras/{camera_id}/snapshot.jpg"))

    def camera_pose(self, camera_id):
        return request("GET", self._url(f"/api/cameras/{camera_id}/pose"))

    def walker_pose(self, walker_id):
        return request("GET", self._url(f"/api/walkers/{walker_id}/pose"))

    def wait_idle(self, camera_id, timeout=10.0, settle=0.3):
        """Waits until the head has stopped; returns its pose."""
        deadline = time.monotonic() + timeout
        time.sleep(0.1)
        while time.monotonic() < deadline:
            pose = self.camera_pose(camera_id)
            if not pose.get("moving"):
                time.sleep(settle)
                return self.camera_pose(camera_id)
            time.sleep(0.05)
        raise RuntimeError(f"camera {camera_id} is still moving after {timeout:.0f} s")
