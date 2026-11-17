# The demo through the browser

`check_demo.py` walks the demo over REST, so it proves the API answers. It cannot prove that the
operator's click reaches that API — and that is where the bugs live. M2.11's part placement worked
in the code and not in the GUI: the palette's pick never armed the click, and the landmarks kept
catching it. No API check could have noticed.

This script closes that gap and nothing more. It is an **acceptance tool, not a regression suite**:
it needs the whole dev stack and the simulator, it is slow, and it changes the demo (and puts it
back). Run it by hand at a milestone, beside `check_demo.py`. It is deliberately not wired into CI.

## Running it

The stack must be up — `scripts/dev-up.sh` (or F5) and the OnvifSim demo world installed through
`tests/demo/check_demo.py --provision-cameras` — and Vite must serve the client on 5173.

```sh
cd tests/demo/gui
npm install
npx playwright install chromium     # once per machine
node check_gui.cjs
```

Environment: `VMS_WEB` (default `http://localhost:5173`), `VMS_USER` / `VMS_PASS` (default
`admin`/`admin`), `SHOTS` (screenshots, default `./shots`, ignored by git). The exit code is the
number of failed checks turned into 0/1; a failing run prints where the screenshots went.

## What it checks

- the world's own `relay4 rep1` on `map1`, switched on and off from the map inspector;
- the camera put on guard from the Control panel: the closed sensor adds an alarm row the map lists;
- a part picked from the palette (`Admin → Maps → Devices`) arms the click, and the click on empty
  map places a replica the server names;
- the camera's rear panel reached from the map: one element per bound part, all five of them;
- the demo put back at the end: relay off, camera off guard.

## Writing more of it

Deep links are the way in: `/operator/map?node=<id>` opens the map on that node, and the journal
uses the same one. Read the ids at runtime (`demoNodes`), never from a constant — a space's id is
the server's and a camera's picture is made anew on every install.

Two traps cost a run each when this was written. An MUI switch is an `input[type=checkbox]` with no
`aria-checked`: use `isChecked()`, not `getAttribute`. And the camera panel's socket names are
painted into its background picture — an `<img>` — so assert the part elements the page puts in the
DOM, not the words on the drawing.
