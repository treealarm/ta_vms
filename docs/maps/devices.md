# Cameras and detections on the map

> Part of [Maps & plans](../maps-and-plans.md): the model and where things live are there.

## Cameras that report themselves

A camera says more about itself than its position: it reports the live state of its relay outputs
and digital inputs, and where a mounted device's camera stands. Both are **property events**, not
getters — ONVIF's `GetRelayOutputs`/`GetDigitalInputs` return configuration (mode, idle state), and
there is no `GetRelayState` at all.

- **Events.** `Device/Trigger/Relay` and `Device/Trigger/DigitalInput` carry `Source/*Token` and
  `Data/LogicalState`. On subscription the camera replays the current state as
  `PropertyOperation="Initialized"` — that is the initial value, the thing a getter would have
  given — and later changes arrive as `"Changed"`. VmsOnvif's `DeviceIoStateService` opens a
  topic-filtered pull point for any camera whose relay or input list is non-empty, keeps the last
  state per `(camera, kind, token)`, and closes the subscription once both lists are empty again.
  It is separate from the optional alarm/analytics subscription (`onvif_events_enabled`): I/O state
  is part of the integration, not an opt-in.
- **`live_active`.** `GetRelayOutputs`/`GetDigitalInputs` fill it from that cache, and the field is
  absent until the camera has reported the device — "unknown" is not "inactive". The web shows it
  as each relay's reported state beside the command switch, and as the sensor's own `1`/`0`.
- **Polling is ours, not the camera's.** The camera pushes; the pages read VmsOnvif's cache every
  five seconds while they are visible (`DeviceIoSection`, the wall tile's indicator, the demo
  feeder). A settings read never waits on the camera for a live value.
- **`geo_from_device`.** A camera can be told that its place on the map is its own ONVIF position.
  Off by default, because a hand-placed camera is calibrated and aimed by survey and must not drift
  with a coordinate the device happens to report. While on, `CameraGeoFollower` reads
  `GetCameraGeoLocation` every five seconds and moves **only the point** of the reference replica —
  never the canonical node, the parent, height, heading or field of view — and only when it moved more than
  half a metre, publishing `map-config-changed` with both the node and the camera id so the
  browser reloads placements instead of waiting for its 30-second poll. Geographic calibration
  (ground marks, PTZ landmark observations) and aiming (a point on the map, a GEO master/slave
  pair) refuse such a camera: a fit describes the point it has left. Self-guidance is unaffected —
  it works on the camera's own picture and uses no place.
- **Wires are the simulator's, not ta_vms's.** In OnvifSim a relay's contact can be wired to the
  digital inputs of any camera and to lamps of the world (an input reads the inversion of its
  pull-up; a lamp follows its NO/NC rule). ta_vms shows only the two ends: a relay is a camera's
  output, an input is a camera's input, and what changes one appears on the other through the
  events above.


## Detections on the map

The operator's map shows the live detections of every fixed camera mapped onto the ground whose
reference placement shares geometry with the space on screen (VmsCfg decides which, by the same
rule as everything else). "Detections" in the map's corner turns them off and on.

- The browser gets each camera's mapping as a 3×3 matrix straight to WGS84 (`GET
  api/ground-geo?spaceId=`) and only multiplies by it; the fit is the server's. Detections come
  from the same live stream the video overlay uses (`api/analytics/live`), with the same fade.
- The point projected is the bottom middle of the box — feet, wheels. Faces and plates are not
  drawn: they sit on a person or vehicle that already is.
- A point beyond the horizon, or farther than the camera's range on its placement, is dropped: near
  the horizon one pixel spans kilometres, and such a point says more about the fit than about the
  object.
- A dot has its track's colour, the same as its box on the video, so the two can be matched.
