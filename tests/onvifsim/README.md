# A virtual bench for PTZ aiming

There is no PTZ camera on the bench that this project can reach. Before this harness existed, the
aiming code was checked only with synthetic numbers.
[OnvifSim](https://github.com/treealarm/OnvifSim) closes that gap. It is a 3D world whose cameras are
real ONVIF devices: RTSP H.264, PTZ with AbsoluteMove and GetStatus, and an ONVIF metadata track that
boxes the people in view. It also says where everything really is.

The harness builds one small world, wires it into a running ta_vms, calibrates the cameras through
the ordinary ONVIF path, and measures where each PTZ actually looks. It does so twice, for two
master/slave pairs watching the same yard: one on a map in real coordinates, the other on a plan in
made-up ones.

## The world

`world.py` describes it. The harness and the checks read it from there, so the two cannot drift
apart. Run `python3 tests/onvifsim/world.py` to print the expected numbers.

| What | Where (metres: X east, Z south, Y up) |
|---|---|
| `master`, a fixed camera | (0, 6, 20), facing north, pitched 18° down, 70° wide |
| `ptz`, a PTZ camera | (28, 9, −14), mounted facing 240°, 60° wide at zoom 0, no analytics of its own |
| `master2`, the plan's fixed camera | (−26, 6, −34), facing 134°, pitched 18° down, 70° wide |
| `ptz2`, the plan's PTZ camera | (−24, 8, 18), mounted facing 45°, otherwise like `ptz` |
| `topdown`, the plan's photographer | 120 m above (0, −6), looking straight down, 1280×960; never added to ta_vms |
| Landmarks, half-metre boxes | L1 red (−9, 4), L2 yellow (9, 4), L3 blue (−13, −12), L4 white (13, −12), L5 purple (0, −24) |
| `w1`, one person | walks the square (−8, 0) → (8, 0) → (8, −16) → (−8, −16) at 1 m/s, along a sand path |
| Scenery | a row of pines to the north, three houses, oaks, rocks and a lane behind the master |

The ground is flat. The landmarks carry their colour in their names in ta_vms, so the one on the
map is easy to find in the picture. The scenery is there to find one's way by, and `world.py`
checks that none of it stands on a camera's line of sight to a landmark or to any point of the
person's loop. Only the master boxes people: the slave is aimed by the master's boxes alone, and
boxes on its own video would only suggest otherwise. The sim's origin sits at 39.2° N, 77.35° W, on
farmland in Maryland, which is off the equator so that a missing cos(latitude) shows up as an error.
ta_vms gets a map named "OnvifSim lab" with the five landmarks and the reference placements of
`master` and `ptz`, and a plan named "OnvifSim lab plan" with the same five landmarks and the
reference placements of `master2` and `ptz2`.

## The plan

A plan without real coordinates keeps its positions as made-up WGS84 around (0, 0), and shares
geometry with nothing but itself (see `docs/maps-and-plans.md`). The second pair is there to prove
both halves of that: master and slave on the same such plan aim at each other, and a master on the
map is refused for a slave on the plan.

The plan's picture is a snapshot from `topdown`, taken with nobody in the yard. Its top is north and
its right is east. The ground is flat, so the frame's corners land exactly at 120·tan(hfov/2) from
the centre, 96 m by 72 m, and the harness gives ta_vms those three corners when it uploads the
picture. The ground, the path and the feet of the landmarks therefore sit exactly where the plan
says. Houses and trees lean slightly outwards, as anything standing up does in a photograph from
above; that is why the camera is high and narrow.

## Running it

You need these:

- The ta_vms dev stack, running: `docker compose up -d postgres rabbitmq mediamtx db-init`, then
  `scripts/dev-up.sh` or F5.
- `../OnvifSim` checked out next to this repository, and Docker. `up` builds the simulator's image
  from that checkout (`docker-compose.yml` here) rather than pulling the released one: the bench
  needs the ground-truth API, which a release may not have yet. The image carries ffmpeg, which
  video_srv's H.264 profile needs.

```
tests/onvifsim/onvifsim_lab.py all              # every step below in order, then down
tests/onvifsim/onvifsim_lab.py up               # each step on its own
tests/onvifsim/onvifsim_lab.py provision [--reset]
tests/onvifsim/onvifsim_lab.py calibrate [--no-zoom]  # --no-zoom: no zoom samples, slaves stay wide
tests/onvifsim/onvifsim_lab.py aim-check [--seconds 60]
tests/onvifsim/onvifsim_lab.py bind
tests/onvifsim/onvifsim_lab.py run [--seconds 60]
tests/onvifsim/onvifsim_lab.py down [--purge]   # --purge also removes the cameras and both spaces
tests/onvifsim/onvifsim_lab.py matrix [--mounts a,b] [--seconds 30]   # the map pair hung every way, see Mounts

Every step works on both pairs, one after the other; --pair map or --pair plan picks one.

After `bind` the configuration stays in ta_vms: restart the stack, and the slave follows the
person again without any of the steps before it.
```

OnvifSim runs in the container `onvifsim-lab-onvifsim-1`, its ports mapped one to one and clear of
the dev stack: 8090 for the editor and API, 8560 for RTSP, 6980 for RTP, and 8101–8104 for ONVIF
(`master`, `ptz`, `master2`, `ptz2`; `topdown` gets 8105, which is not mapped out). Open
http://localhost:8090 to watch the world while a step runs; its log is
`docker compose -f tests/onvifsim/docker-compose.yml logs`. The steps keep what they created in
`/tmp/onvifsim-lab/state.json`, by pair. The ta_vms camera ids are `sim-master`, `sim-ptz`,
`sim-master2` and `sim-ptz2`.

## What each step proves

- **provision** adds the cameras with an ONVIF connection and the `Profile_2` token. The stream
  URI therefore comes from `GetStreamUri` through VmsOnvif. It creates the plan and uploads its
  picture the first time, or again with `--reset`. The step passes only when a person box from
  each master has come back through video_srv, VmsOnvif and the analytics service to
  `/api/analytics/live`.
- **calibrate, PTZ part.** For each landmark it turns the sim's head onto it, as a person would
  with a joystick. It then reads the position back through `GET /api/ptz/status`, which is ONVIF
  `GetStatus` via VmsOnvif. That reading must equal the generic position the sim's degrees imply,
  to 1e-3. Units or sign going wrong anywhere on that path stops it here.
- **calibrate, zoom samples.** At each landmark it then zooms the head until the picture is
  `FRAME_WIDTH_M` (8 m) wide at the landmark's distance: a person, plus the 2 m they walk between
  two commands either side, plus room for the aim's error. It reads that zoom back through
  `GET /api/ptz/status` and records it with the observation, as an operator's *Remember zoom* does.
  The distance ta_vms reports for each landmark must match the world's to 0.1 m. The sim's lens
  steps its focal length by equal ratios, so ta_vms's interpolation in ln(distance) between these
  samples gives the same 8 m frame at any distance between them.
- **calibrate, ground part.** It stands a person on each landmark and takes the bottom-middle of
  the box ta_vms received as the mark. The leave-one-out PTZ residuals and the ground residuals
  must both be near zero, because this world has no lens distortion and, unless a mount says
  otherwise, no tilted mount.
- **aim-check** follows the walking person with `POST /api/ptz-geo/sim-ptz/aim`, the same call the
  operator's map makes. Each time the head stops, it compares the PTZ's true optical axis, from
  the sim's `/api/cameras/ptz/pose`, with the direction to the person's feet.
  - *Aim error* is measured against where the person was when the command was sent. It is the
    precision of the calibration and the geometry, and it should be well under a degree.
  - *Lag error* is measured against where the person is by the time the head arrives.
  - The head's zoom must be what the samples give for the person's distance, to 0.03, and the
    widest sample when the aim is outside the landmarks.
  - The step also checks, from the sim's true pose of the slave, that the whole person, feet to
    head, is in its picture. The slave has no analytics of its own in this world: only the
    master's boxes drive it, and boxes on the slave's video would only suggest otherwise.

- **bind** binds the PTZ to the master by the method `geo` (through the map, no survey) and
  switches tracking on for people from the master's ONVIF boxes. It passes when the binding
  reports nothing missing and the tracking service runs a session for the slave without an error.
  For the plan's pair it first binds `ptz2` to the map's `master`, and aims `ptz2` at a point of
  the map. Both must be refused because the two spaces share no coordinates.
- **run** lets ta_vms follow the person on its own and samples the slave's true pose four times a
  second.
  - *Arrival error* is the angle between where the head stopped and where the person was when it
    set off. That is the precision of the whole chain: the master's box, its ground calibration,
    the map, the slave's calibration and ONVIF AbsoluteMove.
  - *Tracking error* is measured while the head is at rest against where the person is now. Most
    of it is the slave's two seconds between commands; with zoom samples its dead zone is half a
    metre on the ground here, without them 0.02 of the head's units (3.6° of pan).
  - The whole person must stay in the slave's picture, and with zoom samples fill a quarter of
    its height or more (median). Framed 8 m wide, a person fills about 40%; at the wide end, under
    10%.

## Mounts

Real cameras do not all hang upright on a pole, and they do not all count their axes the same way:
ONVIF's generic space says nothing about which way pan grows, a camera hung upside down may or may
not have its picture and its axes turned back by the installer, and a bracket may lean. OnvifSim
hangs a camera any way up (its mount is a rigid body that the head turns with), turns its picture
(`video.rotate`) and reverses an ONVIF axis (`ptz.panReverse`, `ptz.tiltReverse`); `world.MOUNTS`
names the ways worth checking, applied to the map pair:

| Mount | What it is | Expected |
|---|---|---|
| `level` | upright, as above | pass |
| `pan-reverse`, `tilt-reverse` | ONVIF counts that axis the other way | pass |
| `install-mode` | upside down; picture and both axes turned back, as an installer sets it | pass |
| `auto-flip` | upside down; picture turned back, axes left mirrored | pass |
| `upside-down-raw` | upside down, nothing turned back | pass |
| `lean-forward`, `lean-sideways` | the pan axis leaning 10° towards the yard, 8° sideways | measure |
| `fence` | on its side: pan sweeps up and down, tilt across | measure |
| `seam` | turned so that pan ±180 falls among the landmarks | pass |
| `master-portrait` | the master on its side for a corridor, picture upright and tall (360×640) | measure, clicked marks |
| `master-flip` | the master's picture left upside down | measure, clicked marks |

A mount hung upside down gets the tilt range turned over with it (−20..90), and `fence` ±90. The
two master mounts take their ground marks where the landmarks' feet really are in the picture, as
an operator clicks them, instead of from the boxes of a person standing there: in the tall frame a
person beyond about 28 m is smaller than OnvifSim's smallest box (2.5% of the frame's height), and
in the upside-down one the box's foot is the head. A "pass" mount must meet every threshold of
every step. A "measure" one is a mount the current fits
are known not to model: its thresholds become notes, so its steps run to the end and say what the
fits make of it.

- `--mount <name>` hangs the map pair that way for the ordinary steps; `up --mount <name>` loads it.
- `matrix` runs them all (or `--mounts a,b`) on the map pair, against the running sim: for each it
  rehangs the cameras with `PUT /api/cameras/{id}`, drops the old observations, and runs the PTZ
  calibration, aim-check, bind and run; the ground marks again only when the master changed. The
  landmarks are centred by OnvifSim's own `POST /api/cameras/{id}/ptz/aim`, which knows how its
  head hangs, and the readings still go through ONVIF and VmsOnvif. All the projections use the
  picture's own axes from `/pose`. It prints one table, writes it to `/tmp/onvifsim-lab/matrix.txt`
  (and `matrix.json`), leaves the pair level and calibrated again, and fails only when a "pass"
  mount did not pass.

### What the mounts do to the aim (2026-10-18)

`matrix --seconds 30` on the map pair; `master-portrait` alone with `--seconds 70`, so the person
also walks the near half of the loop, the only half its tall frame detects. PTZ LOO is the worst
leave-one-out residual in generic units; foot is how far the box's foot lay from the feet in the
master's picture; arrival is where the head stopped against where the person was when it set off.

| Mount | PTZ LOO | Foot | Aim mean / p95 | Arrival mean / p95 | In frame | Verdict |
|---|---|---|---|---|---|---|
| `level` | 0.0000 | 0.007 | 0.00° / 0.00° | 0.24° / 0.43° | 100% | pass |
| `pan-reverse` | 0.0000 | | 0.00° / 0.00° | 0.21° / 0.39° | 100% | pass |
| `tilt-reverse` | 0.0000 | | 0.00° / 0.00° | 0.21° / 0.42° | 99% | pass |
| `install-mode` | 0.0000 | | 0.00° / 0.00° | 0.18° / 0.39° | 100% | pass |
| `auto-flip` | 0.0000 | | 0.00° / 0.00° | 0.24° / 0.41° | 99% | pass |
| `upside-down-raw` | 0.0000 | | 0.00° / 0.00° | 0.16° / 0.36° | 100% | pass |
| `lean-forward` (10°) | 0.0526 | | 0.61° / 0.86° | 0.73° / 0.95° | 100% | within the thresholds |
| `lean-sideways` (8°) | 0.0943 | | 1.72° / 2.72° | 1.60° / 2.76° | 100% | misses them |
| `fence` | 0.4212 | | 12.9° / 15.9° | 13.3° / 16.0° | 12% | unusable |
| `seam` | 0.0000 | | 0.00° / 0.00° | 0.21° / 0.39° | 100% | pass (was 96° / 135° off before the fit found the seam) |
| `master-portrait` | 0.0000 | 0.007 | 0.00° / 0.00° | 0.18° / 0.34° | 54% | aims right; the far half is undetected |
| `master-flip` | 0.0000 | 0.123 | 0.00° / 0.00° | 27.3° / 35.9° | 0% | unusable |

- Every way of counting the axes and every upside-down mount aims exactly: the two lines take
  the sign and the units.
- A leaning pan axis costs roughly a fifth of a degree per degree of lean sideways and a sixteenth
  forward, here; it shows in the leave-one-out column long before it misses. `fence` is beyond the
  two lines altogether, which is what "PTZ fit by mount orientation" in `docs/maps-and-plans.md` is
  for.
- `seam` is the one that can catch an ordinary camera: its pan zero is wherever the installer
  screwed it, so the seam falls inside the sector it watches with the odds of that sector's share
  of a full turn, a quarter for a camera watching 90°. The fit now finds it among the landmarks
  and folds an aim past ±1 back by a turn; the walk crosses it (pan -0.98, then 1.00) and the
  head follows. Before that it was 96° / 135° off and never framed the person. Without the fold,
  OnvifSim refuses the aims that land between the true seam and where the fit puts it, as a
  camera that checks its range does.
- A master's picture can be turned or tall, the ground mapping does not care. Left upside down, the
  foot of every box is the person's head: marks clicked on the landmarks then put the slave 27° off.
  (Marked with people's boxes instead, the flip cancels out, because the marks and the aim use the
  same edge; an operator clicks the landmarks.)

## Limits

OnvifSim's PTZ is exactly linear in angle, its lens has no distortion and its ground is flat: a
real camera's misses from those (a pan scale that is not quite linear, a wide lens) are not on the
bench. Electronic flip (the head flipping over at the nadir, ONVIF `EFlip`) is not simulated: this
world never aims past straight down. The joystick and the self-guided tracking, which drive the
head by `ContinuousMove` and calibrate it in the picture, are not exercised by any step.
