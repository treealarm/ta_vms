# The demo through the browser

`check_demo.py` walks the demo over REST, so it proves the API answers. It cannot prove that the
operator's click reaches that API — and that is where the bugs live. M2.11's part placement worked
in the code and not in the GUI: the palette's pick never armed the click, and the landmarks kept
catching it. No API check could have noticed.

Two tools close that gap, and neither is a regression suite: both need the whole dev stack and the
simulator, both are slow, and both change the world they look at. Run them by hand at a milestone,
beside `check_demo.py`. They are deliberately not wired into CI.

| Tool | Question it answers |
|---|---|
| `check_gui.cjs` | Do the demo's own elements work when an operator clicks them? |
| `build_world.cjs` + the comparisons below | Can an **administrator build an equivalent world from the GUI alone**? |

## Running the checks

The stack must be up with the demo off (`scripts/dev-up.sh --no-demo`), the OnvifSim lab must serve
the `maryland` world, and Vite must serve the client on 5173.

```sh
cd tests/demo/gui
npm install
npx playwright install chrome      # once per machine; the scripts launch the installed Chrome

python3 ../../onvifsim/onvifsim_lab.py up --world maryland
```

### The parity run

The demo is the baseline: its seeder writes the world through the ordinary services, so the GUI has
to be able to write everything the seeder can. One instance of each primitive is enough — the run
does not reproduce four racks and 192 ports.

```sh
cd /mnt/wdblue/tests/ta_vms

node tests/demo/gui/build_world.cjs --phase demo
python3 tests/demo/gui/parity_inventory.py snapshot --label demo \
  --out tests/demo/gui/out/demo-inventory.json

node tests/demo/gui/build_world.cjs --phase build
python3 tests/demo/gui/parity_inventory.py snapshot --label parity \
  --out tests/demo/gui/out/parity-inventory.json
python3 tests/demo/gui/parity_inventory.py compare \
  tests/demo/gui/out/demo-inventory.json tests/demo/gui/out/parity-inventory.json

python3 tests/demo/gui/check_parity.py --prefix parity
python3 tests/demo/gui/functional_smoke.py --prefix parity
```

Three levels, deliberately separate:

1. **GUI coverage** — `build_world.cjs` creates every element through the browser and screenshots
   each step (`shots/parity/`).
2. **Configuration parity** — `parity_inventory.py` reduces both worlds to descriptors ("what kind
   of thing, related how") read from `ta_map` and `ta_cfg`; `check_parity.py` confirms the result is
   readable through the public API. Both only SELECT or read.
3. **Functional smoke** — `functional_smoke.py` acts on the simulator and checks that the
   configuration does something: a contact raises an alarm and drives the rule's action, the PTZ
   slave follows the master's target, and a `geo_from_device` camera's placement moves with its
   telemetry.

`build_world.cjs --phase build` starts by removing the demo and its cameras: the parity snapshot
must not read the seeder's world. The preflight check (demo absent, no `parity-map`) runs after that
removal, so an automatic re-install would still be caught.

### Phases and environment

```
node build_world.cjs --phase demo       # install the demo; the coverage baseline
node build_world.cjs --phase build      # remove it, then build the parity world (preflight included)
node build_world.cjs --phase cleanup    # delete the map, cameras, wall, subscription, type, layer
node build_world.cjs --phase continue   # finish a half-built world (--steps narrows it)
node build_world.cjs --reset-db --force # drop and recreate ta_map/ta_cfg
```

`--phase continue` runs the steps that a partially built world still needs: `rights`, `calibrate`,
`contact`, `replica`, `icon`, `level`, `wall`, `routing`, `binding`. Pass `--steps routing,binding`
to run some of them.

Environment: `VMS_WEB` (default `http://localhost:5173`), `VMS_USER` / `VMS_PASS` (`admin`/`admin`),
`PREFIX` (`parity`), `ONVIF_HOST` (`127.0.0.1`), `ONVIF_PORT_BASE` (`8101`), `SIM_URL`
(`http://127.0.0.1:8090`), `SIM_PTZ_CAMERA` (`ptz`), `SHOTS` (`./shots/parity`). Screenshots, `out/`
and `shots/` are git-ignored. The exit code is 0 only when every check passed.

### Cleanup and the bench

`--phase cleanup` deletes what the build made through the same surfaces, in the order the tools
require: wall, subscription, scale layer, map, cameras, diagram types. Two things it cannot reach
stay behind: plan and icon files have no DELETE endpoint, and an interrupted run can leave anything
half-written. `--reset-db --force` drops and recreates `ta_map`/`ta_cfg`; restart `vmscfg` and
`vmsmap` afterwards so their migrations run. The guard refuses without `--force`, when the postgres
is not this checkout's compose service, or when an installation from `ta_install` is running.

### The coverage matrix

Every configuration primitive the demo seeds, the GUI path that creates it, and the level that
proves it. Keep it current: a new configuration capability arrives with a GUI path, or it does not
arrive.

| Demo capability | GUI path | Proof |
|---|---|---|
| Map, plan, nested plan, picture, footprint | Admin → Maps / SpaceEditor | build + DB |
| Diagram type, background, regions, styles | Types / DiagramTypeRegionEditor | build + DB |
| Region bound to a part by token | DiagramTypeRegionEditor | DB: binding resolves to the part |
| Diagram: own canvas, parent, region, type | "+" in Nodes / DiagramElementEditor | build + DB + API |
| Device diagram attached by model | camera row → Device diagram | build + DB + API |
| Part replica on the map | Devices → part → click | build + API |
| Camera placement: reference/image, view | Devices → camera → click / placement panels | build + DB |
| Dynamic position `geo_from_device` | "Position from device" on the camera | functional |
| Device part names and behaviour | Device I/O | build + API |
| Landmarks, ground and PTZ calibration | Landmarks / calibration dialogs | API + functional |
| Master/slave, method `geo` | Master / Tracking tabs | DB + functional |
| Auto-tracking of `person`/`onvif` | Tracking | functional |
| Event routing: selector, every action | Event routing | DB + functional |
| Rights on spaces | Rights | API |
| Scale layer and its assignment | Dictionaries + Object → Scale layer | DB |
| Video wall | Monitors | build + DB |
| Free text and picture on a plan | SpaceEditor tools | build + DB |
| States and values | deliberately absent | excluded |

## Traps

Cost one run each when this was written:

- An MUI switch is an `input[type=checkbox]` with no `aria-checked`: use `isChecked()`, not
  `getAttribute`. A switch may need `click({ force: true })` when a tooltip wrapper sits over it —
  and a right that the box does not show must be verified through the API, not by re-reading the box.
- An MUI select is a `div` with `role="combobox"`; click it and then the `role="option"`, never
  `selectOption`.
- Accessible names are case-sensitive with `exact: true`, and innerText may be uppercased by CSS:
  match button names with a case-insensitive regular expression.
- One tab, `domcontentloaded`, and a single page: the SPA holds media sockets and SSE connections,
  and a second tab starves them.
- The camera panel's socket names are painted into its background picture — an `<img>` — so assert
  the part elements the page puts in the DOM, not the words on the drawing.
- Device parts appear after VmsOnvif polls the camera: refresh and wait, do not assume the first
  read has them.
- Calibration marks must span a plane. Four marks on one line are refused by name, and four
  recordings of one pose leave the fit with no direction at all; the PTZ calibration drives the
  simulated camera to a different pose per landmark before recording it.
- The simulator keeps its relay state between runs, and the master's peer relay is wired to the
  slave's input. A contact left closed raises an alarm the moment the input is configured, and that
  alarm outlives the run: both the build and the smoke start from an open contact.
- A placement that follows telemetry is rewritten by the device every few seconds; a level assigned
  to it must survive that move (it does — the move carries the figure's own fields over).

## check_gui.cjs

The demo's own elements, driven as an operator would:

- the world's own `relay4 rep1` on `map1`, switched on and off from the map inspector;
- the camera put on guard from the Control panel: the closed sensor adds an alarm row the map lists;
- a part picked from the palette (`Admin → Maps → Devices`) arms the click, and the click on empty
  map places a replica the server names;
- the camera's rear panel reached from the map: one element per bound part, all five of them;
- the demo put back at the end: relay off, camera off guard.

Deep links are the way in: `/operator/map?node=<id>` opens the map on that node, and the journal
uses the same one. Read the ids at runtime (`demoNodes`), never from a constant — a space's id is
the server's and a camera's picture is made anew on every install.
