# ONVIF live parameters: what is left

Found while doing M2.3 (`docs/plans/square-merge/m2.3-onvif-live-parameters.md`). The milestone
itself is closed; these are the parts its scope deliberately left for later.

## Several devices per camera, inversion and fan-out

ta_vms shows a camera's relay and input state one device at a time:

- `DeviceIoStateService` caches every `(kind, token)` the camera reports, which is right.
- The demo feeder (`DemoValueFeeder`) names the cameras (`DemoTelemetry.RelayCameraId` is
  `sim-master`, `SensorCameraId` is `sim-ptz`) but takes **their first device**. A camera with
  several relays of different meaning needs to name the device too: which relay feeds the "relay"
  reading, which input the "sensor" one.
- The web lists all of them, so this is a demo limitation only, not a UI one.

The wiring itself — one relay to several sinks, one input to several relays, the input reading the
inversion of its pull-up — is OnvifSim's model and is not carried into ta_vms. If a real ONVIF
camera ever needs that, it would have to become camera configuration here (a sink is a device of
another camera, and there is no cross-camera relation today).

## The list read behind the device I/O overlay

`useDeviceIoLive` used to poll VmsOnvif's cache every five seconds (`DEVICE_IO_POLL_INTERVAL_MS`),
and each round went web_srv → VmsOnvif → the camera's `GetRelayOutputs`/`GetDigitalInputs` for the
*configuration* before enriching `liveActive` from the event cache. A wall of N tiles made 2N ONVIF
requests per five seconds, and the value on screen was still up to five seconds old.

Since 2026-11-06 the states are pushed: `DeviceIoService.StreamDeviceIoStates` (VmsOnvif) sends the
current state of every part and every transition, web_srv merges one stream per node into
`/api/DeviceIo/live` (SSE), and the browser keeps one connection for the whole monitor
(`deviceIoLiveClient`). web_srv does not subscribe to `device-event` itself: replicas of one app id
share a consumer group, so each event reaches one replica and the browsers on the others would
never see it. A server stream from every node, by contrast, is delivered to every web_srv replica in
full.

What is left is the **list** read: `useDeviceIoLive` still asks for relays and inputs (mode, delay,
idle state) when a tile mounts, and each call reads the camera. That is once per tile, not per five
seconds, and it also opens the camera's watcher. Serving it from VmsOnvif's cached presence —
`DevicePresence` from the discovery sweep — would remove the camera round trip from mounting a
monitor wall; that is the remaining part of this topic.

## The demo world and `Maryland` match only in camera poses

The simulator world and the demo map are built from the same constants for the building envelope and
the camera poses, and `tests/onvifsim/world.py --check-maryland` checks those within half a metre
and half a degree. The rest does not match: the rooms, the racks and the scenery are drawn
separately, and the demo's indoor plan is not the simulator's render of it. A fuller match would let
the bench check what the demo draws against what the camera sees, without a browser.

## The follower's first write after a restart

`CameraGeoFollower` keeps the last point it wrote in memory, so a still camera costs no tree writes.
After a restart that memory is empty and the camera's first answer is written once, even if it is
the point already stored — which is also what puts the map right after a downtime. On a deployment
with many flagged cameras that is one write and one `map-config-changed` per camera per restart;
reading the stored point to compare against would remove it, at the cost of one query per camera.
