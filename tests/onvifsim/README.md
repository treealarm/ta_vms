# A virtual bench for PTZ aiming

There is no PTZ camera on the bench that this project can reach. Before this harness existed, the
aiming code was checked only with synthetic numbers.
[OnvifSim](https://github.com/treealarm/OnvifSim) closes that gap. It is a 3D world whose cameras are
real ONVIF devices: RTSP H.264, PTZ with AbsoluteMove and GetStatus, and an ONVIF metadata track that
boxes the people in view. It also says where everything really is.

The harness builds one small world, wires it into a running ta_vms, calibrates both cameras through
the ordinary ONVIF path, and measures where the PTZ actually looks.

## The world

`world.py` describes it. The harness and the checks read it from there, so the two cannot drift
apart. Run `python3 tests/onvifsim/world.py` to print the expected numbers.

| What | Where (metres: X east, Z south, Y up) |
|---|---|
| `master`, a fixed camera | (0, 6, 20), facing north, pitched 18° down, 70° wide |
| `ptz`, a PTZ camera | (28, 9, −14), mounted facing 240°, 60° wide at zoom 0, no analytics of its own |
| Landmarks, half-metre boxes | L1 red (−9, 4), L2 yellow (9, 4), L3 blue (−13, −12), L4 white (13, −12), L5 purple (0, −24) |
| `w1`, one person | walks the square (−8, 0) → (8, 0) → (8, −16) → (−8, −16) at 1 m/s, along a sand path |
| Scenery | a row of pines to the north, three houses, oaks, rocks and a lane behind the master |

The ground is flat. The landmarks carry their colour in their names in ta_vms, so the one on the
map is easy to find in the picture. The scenery is there to find one's way by, and `world.py`
checks that none of it stands on a camera's line of sight to a landmark or to any point of the
person's loop. Only the master boxes people: the slave is aimed by the master's boxes alone, and
boxes on its own video would only suggest otherwise. The sim's origin sits at 41.3° N, 69.24° E,
which is off the equator so that a missing cos(latitude) shows up as an error.
ta_vms gets one map named "OnvifSim lab" with the five landmarks and both cameras' reference
placements.

## Running it

You need these:

- The ta_vms dev stack, running: `docker compose up -d postgres rabbitmq mediamtx db-init`, then
  `scripts/dev-up.sh` or F5.
- `../OnvifSim` checked out next to this repository, with .NET 10 and ffmpeg. media_server pulls the
  H.264 profile, which OnvifSim offers only when it finds ffmpeg.

```
tests/onvifsim/onvifsim_lab.py all              # every step below in order, then down
tests/onvifsim/onvifsim_lab.py up               # each step on its own
tests/onvifsim/onvifsim_lab.py provision [--reset]
tests/onvifsim/onvifsim_lab.py calibrate
tests/onvifsim/onvifsim_lab.py aim-check [--seconds 60]
tests/onvifsim/onvifsim_lab.py bind
tests/onvifsim/onvifsim_lab.py run [--seconds 60]
tests/onvifsim/onvifsim_lab.py down [--purge]   # --purge also removes the cameras and the map

After `bind` the configuration stays in ta_vms: restart the stack, and the slave follows the
person again without any of the steps before it.
```

OnvifSim runs on ports clear of the dev stack: 8090 for the editor and API, 8560 for RTSP, 6980
for RTP, and 8101/8102 for ONVIF. Open http://localhost:8090 to watch the world while a step runs.
Its log and data are in `/tmp/onvifsim-lab/`. The ta_vms camera ids are `sim-master` and `sim-ptz`.

## What each step proves

- **provision** adds both cameras with an ONVIF connection and the `Profile_2` token. The stream
  URI therefore comes from `GetStreamUri` through VmsOnvif. The step passes only when a person box
  from the master has come back through media_server, VmsOnvif and the analytics service to
  `/api/analytics/live`.
- **calibrate, PTZ part.** For each landmark it turns the sim's head onto it, as a person would
  with a joystick. It then reads the position back through `GET /api/ptz/status`, which is ONVIF
  `GetStatus` via VmsOnvif. That reading must equal the generic position the sim's degrees imply,
  to 1e-3. Units or sign going wrong anywhere on that path stops it here.
- **calibrate, ground part.** It stands a person on each landmark and takes the bottom-middle of
  the box ta_vms received as the mark. The leave-one-out PTZ residuals and the ground residuals
  must both be near zero, because this world has no lens distortion and no tilted mount.
- **aim-check** follows the walking person with `POST /api/ptz-geo/sim-ptz/aim`, the same call the
  operator's map makes. Each time the head stops, it compares the PTZ's true optical axis, from
  the sim's `/api/cameras/ptz/pose`, with the direction to the person's feet.
  - *Aim error* is measured against where the person was when the command was sent. It is the
    precision of the calibration and the geometry, and it should be well under a degree.
  - *Lag error* is measured against where the person is by the time the head arrives.
  - The step also checks, from the sim's true pose of the slave, that the person is in the
    middle third of its picture. The slave has no analytics of its own in this world: only the
    master's boxes drive it, and boxes on the slave's video would only suggest otherwise.

- **bind** binds the PTZ to the master by the method `geo` (through the map, no survey) and
  switches tracking on for people from the master's ONVIF boxes. It passes when the binding
  reports nothing missing and the tracking service runs a session for the slave without an error.
- **run** lets ta_vms follow the person on its own and samples the slave's true pose four times a
  second.
  - *Arrival error* is the angle between where the head stopped and where the person was when it
    set off. That is the precision of the whole chain: the master's box, its ground calibration,
    the map, the slave's calibration and ONVIF AbsoluteMove.
  - *Tracking error* is measured while the head is at rest against where the person is now. Most
    of it is the slave's dead zone (3.6° of pan on this camera) and its two seconds between
    commands.
  - The person must stay in the middle third of the slave's picture.

## Limits

OnvifSim's PTZ is exactly linear: pan is the bearing minus the mount heading, and tilt is the
elevation minus the mount pitch. This world therefore cannot exercise a camera hung out of level,
which the future mount-orientation fit in `docs/maps-and-plans.md` is for. A tilted-mount mode in
OnvifSim would be the way to test that later.
