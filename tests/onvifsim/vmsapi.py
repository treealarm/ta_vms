"""ta_vms through its gateway (web_vms.Server), the same REST API the browser uses."""

from labhttp import HttpError, request, sse


class Vms:
    def __init__(self, base_url, user, password):
        self.base = base_url.rstrip("/")
        self.user = user
        self.password = password
        self.token = None

    def _url(self, path):
        return f"{self.base}{path}"

    def _headers(self):
        return {"Authorization": f"Bearer {self.token}"} if self.token else {}

    def call(self, method, path, body=None, timeout=15.0):
        return request(method, self._url(path), body, self._headers(), timeout)

    def login(self):
        """Logs in when the gateway has auth on; runs without a token when it says auth is off (503)."""
        try:
            data = request("POST", self._url("/api/auth/login"), {"username": self.user, "password": self.password})
            self.token = data["access_token"]
        except HttpError as e:
            if e.status != 503:
                raise
            self.token = None

    # ── Cameras ─────────────────────────────────────────────────────────────────

    def cameras(self):
        return self.call("GET", "/api/live/cameras") or []

    def add_camera(self, setup):
        result = self.call("POST", "/api/live/cameras", setup)
        if isinstance(result, dict) and result.get("success") is False:
            raise RuntimeError(f"adding camera {setup['cameraId']} failed: {result.get('message')}")

    def delete_camera(self, camera_id):
        self.call("DELETE", f"/api/live/cameras/{camera_id}")

    # ── Spaces, landmarks, placements ───────────────────────────────────────────

    def spaces(self):
        return self.call("GET", "/api/spatial/spaces") or []

    def upsert_space(self, space):
        return self.call("PUT", "/api/spatial/spaces", space)

    def delete_space(self, space_id):
        self.call("DELETE", f"/api/spatial/spaces/{space_id}")

    def landmarks(self, space_id):
        return self.call("GET", f"/api/spatial/spaces/{space_id}/landmarks") or []

    def upsert_landmark(self, landmark):
        return self.call("PUT", "/api/spatial/landmarks", landmark)

    def upsert_placement(self, camera_id, space_id, placement):
        self.call("PUT", f"/api/spatial/placements/{camera_id}/{space_id}", placement)

    # ── PTZ and calibration ─────────────────────────────────────────────────────

    def ptz_status(self, camera_id, profile_token):
        return self.call("GET", f"/api/ptz/status?cameraId={camera_id}&profileToken={profile_token}")

    def put_ptz_geo(self, camera_id, observations):
        """Replaces the observations; the fit (computed by the tracking service) comes from a GET."""
        self.call("PUT", f"/api/ptz-geo/{camera_id}", {"observations": observations})
        return self.get_ptz_geo(camera_id)

    def get_ptz_geo(self, camera_id):
        return self.call("GET", f"/api/ptz-geo/{camera_id}")

    def aim_world(self, camera_id, space_id, lat, lon, height_m=0.0):
        return self.call("POST", f"/api/ptz-geo/{camera_id}/aim",
                         {"spaceId": space_id, "lat": lat, "lon": lon, "heightM": height_m})

    def put_ground_geo(self, camera_id, observations):
        """Replaces the marks; the fit comes from a GET."""
        self.call("PUT", f"/api/ground-geo/{camera_id}", {"observations": observations})
        return self.get_ground_geo(camera_id)

    def get_ground_geo(self, camera_id):
        return self.call("GET", f"/api/ground-geo/{camera_id}")

    # ── Master/slave ────────────────────────────────────────────────────────────

    def put_binding(self, slave_id, binding):
        return self.call("PUT", f"/api/ptz-slave-binding/{slave_id}", binding)

    def get_binding(self, slave_id):
        return self.call("GET", f"/api/ptz-slave-binding/{slave_id}")

    def delete_binding(self, slave_id):
        self.call("DELETE", f"/api/ptz-slave-binding/{slave_id}")

    def hold(self, slave_id, seconds):
        """Keeps a slave's own session off the motor for a while (0 releases), as the wizards do."""
        self.call("POST", f"/api/ptz-slave-binding/{slave_id}/hold", {"seconds": int(seconds)})

    def put_policy(self, camera_id, policy):
        return self.call("PUT", f"/api/ptz-tracking-policy/{camera_id}", policy)

    def tracking_states(self):
        return self.call("GET", "/api/ptz-tracking") or []

    # ── Detections ──────────────────────────────────────────────────────────────

    def live_detections(self, seconds):
        """Yields every detection the gateway streams (0..1 boxes from the top-left)."""
        for _, data in sse(self._url("/api/analytics/live"), self._headers(), seconds=seconds):
            if isinstance(data, dict):
                yield data
