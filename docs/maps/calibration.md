# Calibration, aiming and the algorithms behind them

> Part of [Maps & plans](../maps-and-plans.md): the model and where things live are there.

## Calibrating a PTZ against landmarks

1. Place the camera where it really stands (its reference placement) and set its height above the
   floor.
2. Drop landmarks in the same space at points the camera can see — corners, poles, hatches — with
   their height above the floor.
3. In the camera's panel, *Calibrate by landmarks* (offered on the reference placement only): turn the camera until a landmark sits in the
   crosshair and record it. Two landmarks in clearly different directions make a calibration; a
   third lets each be checked (the "Check" column is the leave-one-out miss — redo the largest).
   While the dialog is open the camera is held for it alone (`api/ptz-tracking/{id}/hold`): neither
   a slave session nor the camera's own self-guidance moves it, including a session that starts
   meanwhile.
4. Picking another landmark, in the table or on the map, turns the camera to it. Once there is a
   calibration that is the calibration alone, with the zoom of the zoom samples, and it is also
   the test that the camera obeys absolute positions at all. Before that, it is back to where the
   landmark was recorded, if it was. Clicking the landmark already picked does not move the
   camera, so a position centred by hand is not lost on the way to *Record*.
5. For a camera that will be a slave, optionally: on a recorded landmark, zoom until a person
   standing there would fill the picture the way the slave should show them, and *Remember zoom*.
   This is a zoom sample for that landmark's distance, see [Master/slave through the
   map](#masterslave-through-the-map). It is kept apart from pan and tilt, which are best recorded
   zoomed right in, and re-recording pan and tilt keeps it.

What the fit assumes: a level mount, so pan follows the bearing and tilt the elevation, each by a
straight line in the camera's own units. A camera hung out of level shows up as a pattern of misses
and should be levelled; one mounted on its side or at an angle cannot be calibrated by this fit at
all (see "PTZ fit by mount orientation" under the next stages). The camera's pan seam, where its
counting wraps round, may fall anywhere, among the landmarks too: the fit finds it, given three
landmarks or more (see [Algorithms](#algorithms)).
Cameras whose `GetStatus` is a stub are refused with a message — they can still be aimed by presets
through the slave binding. What the fit computes is under [Algorithms](#algorithms).

**Mounts and the way cameras count their axes.** ONVIF's generic position space says nothing about
direction: one camera's pan grows clockwise, another's anticlockwise; tilt may count up or down; the
units may be -1..1 or degrees. A camera hung upside down is usually set by its installer to turn its
picture back (the video source's `Rotate`) and to count both axes the other way (PTZ
`PTControlDirection` `Reverse`), but not always, and not always both. None of that needs a setting
here: each of the two lines takes its axis' sign and scale from the landmarks, so a reversed axis, a
camera upside down with or without its picture turned and with or without `Reverse`, and degrees
instead of -1..1 all calibrate the same way. ta_vms reads neither `Rotate` nor `Reverse`, and does
not need to. What the lines cannot take is a pan axis that is not vertical — a bracket that leans, a
camera on its side ("on a fence", where pan sweeps up and down and tilt across). A fixed camera's
ground mapping takes its picture as it comes, turned or tall; but its marks and the master's aim
point are the bottom of a person's box, which in a picture left upside down is their head. The bench
in `tests/onvifsim` hangs a pair each of these ways (`onvifsim_lab.py matrix`); its README has what
each does to the aim.


## Calibrating a fixed camera against the ground

A fixed camera sees the floor through a projective transform, so four points known both in its
picture and on the ground fix the mapping between them, whatever the mount, tilt or lens — no
camera model, and no need for the camera's own position to be exact.

1. Place the camera where it really stands (its reference placement). The calibration is offered
   there, and for a camera without PTZ the same button opens this one.
2. Drop landmarks on the ground the camera sees — the same landmarks a PTZ uses.
3. Pick a landmark, click where it **stands** in the live picture — its foot, not its top; its
   height plays no part here — and go on to the next. Marks can be dragged to correct them.
4. Four landmarks spread over the ground, no three on one line, make a mapping. From the fifth,
   the table shows how far the mapping puts each landmark from where it stands, in metres, and the
   map draws that miss as a red line. Once mapped, the pointer over the picture and the live
   detections show on the map beside it.

The miss is taken against the fit of all the marks, as georeferencing tools do, not leave-one-out as
the PTZ fit is: a homography has eight degrees of freedom, so with a handful of marks leaving out a
corner one makes the rest extrapolate, and a single misplaced landmark showed as a 50 m miss on a
correct corner. All the marks must be on one floor (spaces of equal elevation) — the mapping is of
one plane. Lens distortion is not modelled: a wide lens misses more towards the edges of the frame,
which the misses show; landmarks spread over the part of the picture that matters keep it small
there. How the mapping is solved is under [Algorithms](#algorithms).


## Master/slave through the map

A slave bound to its master by the method `geo` is aimed with no survey of the pair. The master's
ground calibration turns the foot of an object into a place, and the slave's landmark calibration
turns that place into a pan and tilt (`Geo/GeoAiming`). Each camera is calibrated alone, so a pair
works the moment both are, and recalibrating one camera fixes it for every pairing it is in.

- **The target is the foot.** It is the bottom middle of the box, put on the master's floor
  (`GroundHomography.FloorElevationM`). The slave therefore looks at feet.
- **Zoom by distance, from samples.** The slave zooms by the zoom samples in its landmark
  calibration (`Geo/ZoomByDistance`). Each sample is the zoom the operator chose at one landmark,
  stored with the observation as `zoom`; its distance is not stored but follows from where the
  camera and the landmark stand. Between samples the zoom is interpolated in ln(distance). An
  object keeps its size when the focal length grows with its distance, and on a lens whose zoom
  steps are equal ratios that makes zoom a straight line in ln(distance). Nothing is assumed about
  the lens beyond that: ONVIF reports zoom in the camera's own units, whose mapping to a field of
  view differs between models, so a sample says directly what to send.
  - Beyond the nearest and the farthest sample the zoom stays at theirs, so the samples are also
    the range the slave may use.
  - An aim outside the landmarks' sector (*extrapolated*) uses the widest sample, and so does a slave
    whose target is lost: it keeps pointing where the target was last seen but zooms out, so the
    next object nearby is in the picture.
  - With no samples the slave is sent to zoom 0, the wide end. An absolute move always carries a
    zoom, so "leave it as it is" is not something the slave can ask for.
- **Blocked with a reason.** The pair does not run, and the reason shows in the binding's
  `unusable` and the tracking state, when any of these holds:
  - either camera has no reference placement;
  - the two reference spaces share no geometry;
  - the master has no ground fit;
  - the slave has no landmark fit.
- **Cached models.** The aiming service holds both cameras' spatial models in `SpatialModelCache`.
  `map-config-changed` marks the cameras it names stale, and an empty list marks all of them stale.
  There is a one-minute TTL as a backstop. Like every cache in the service
  (`TtlCache`), a stale entry is kept until a read replaces it. A failed read keeps the camera
  aiming by what it had, and is reported as "could not be read", never as "not calibrated".
- **One object per slave.** `SlaveAim` is the binding plus, on this method, both models. It
  answers "can this aim" and "where does this spot send the camera" for every method. The reconcile
  replaces it whole, so a detection is never aimed by a new binding through old calibrations.
  Changing the master restarts the slave's session, since detections are routed by master.
- **Same law as a survey, except the dead zone of a slave that zooms.** The command interval and
  target choice are those of every slave (`SlaveAimingSession`). A slave with zoom samples measures
  its dead zone on the ground rather than in 0.02 of the head's units: a zoomed frame can be
  narrower than 0.02 of pan, which is 3.6° on a head with a full turn, while a frame sampled to fit
  a person is a stretch of ground at any distance. The threshold is 0.5 m, raised to what the
  master's box jitter (0.4% of its frame height) amounts to on the ground at the object, which grows
  with the square of the distance from the master. A change of zoom alone of 0.02 also counts. A
  slave without samples keeps 0.02 of the head's units. A changed binding, placement, observation,
  mark or zoom sample reopens the dead zone; the same calibrations merely read again do not.
- **Checking it.** The binding's tab shows both pictures and no survey. A click on the master's
  ground sends the slave there through both calibrations. The tab re-reads the binding's status
  when it comes back into view, since what is missing is usually fixed on Maps & plans.

It was proved on a virtual bench, [`tests/onvifsim`](../../tests/onvifsim/README.md). In the OnvifSim
world that bench builds, the head arrives within about a quarter of a degree of the person it was
sent to. With zoom samples framing 8 m at each landmark, the whole person stays in the slave's
picture in over 99% of samples and fills a median 38% of its height, against under 10% at the
wide end. The bench
runs a second pair over the same yard on a plan in made-up coordinates, whose picture is the yard
photographed from above, and checks there that a master on the map is refused for a slave on the
plan.


## Algorithms

What the calibrations and the aiming compute, for a reader who wants to know what is exact and
what is a model. Everything is in `dotnet/VmsPtzTrackingSingleton/Geo` unless said otherwise.

**The frame** (`EnuFrame`). Every camera has its own equirectangular east-north frame centred on
itself, on a sphere of radius R = 6 378 137 m: a metre of north is 1/(R·π/180) degrees of latitude,
and a metre of east that divided by cos(lat₀). The error is millimetres across a few hundred
metres and about a metre at 2 km, well inside what a PTZ can be aimed to. A plan in made-up
coordinates lives near (0, 0), where cos ≈ 1, so its made-up degrees behave as metres through the
same formula: there is no second coordinate system anywhere.

**A bearing** (`PtzGeoMath.BearingTo`). The earth is flat within a frame: azimuth is
atan2(ΔE, ΔN) folded into [0, 360), elevation is atan2(ΔH, horizontal distance), where each
height is the floor of the camera's space plus the object's height above that floor.

**A PTZ against landmarks** (`PtzGeoMath`). Two independent straight lines, each fitted by
ordinary least squares:

    pan  = a₁ + b₁ · ((azimuth − seam) mod 360)
    tilt = a₂ + b₂ · elevation

`seam` is where the camera's pan wraps round. A head that pans endlessly reports a jump of its whole
range there, wherever the installer happened to point it, so it is found rather than assumed: it
lies in one of the gaps between neighbouring landmarks (round the circle), and only in that gap do
the readings make one straight line — in any other the jump is a step no line fits. Each gap is
tried, its middle taken as the seam, and the least-squares line that misses least wins; that needs
no knowledge of how long a turn is in the camera's units. Two landmarks fit either way round, and
then the seam is put in the wider gap, behind them. A target between the middle of the seam's gap
and the true seam then comes out past the end of the camera's range, which a camera that checks its
range refuses (OnvifSim does). In ONVIF's generic space −1..1 is the whole range by definition, so
when the slope makes a turn 2 there — a head that pans round — such an aim is folded back by a turn;
a camera counting in other units is left as it is. The slopes absorb whatever units the camera
reports in and the sign of its mount. The fit needs two landmarks at least 5° apart in azimuth and
1° in elevation, and readings that actually move between them — a camera whose `GetStatus` is a stub
is refused on that. *Extrapolated* means the target's azimuth or elevation falls outside the range
the landmarks cover, with 1° of slack; in azimuth that is the circle less its widest gap, so a
sector that straddles north is one sector. The "Check" column is leave-one-out: the fit without that
landmark, evaluated at its bearing, as a miss in pan and one in tilt, in the camera's own units;
with only two landmarks there is nothing to leave out. The seam stays where all the landmarks put it
when one is left out. What the lines do not know: a mount that is not level, which shows as a
pattern of misses rather than a few bad ones (see "PTZ fit by mount orientation" under the next
stages).

**A fixed camera against the ground** (`GroundHomography`). A plane seen by a pinhole camera is a
projective transform of the picture, so the ground mapping is a homography — eight unknowns
with h₃₃ = 1, two linear rows per mark (the direct linear transform). Both point sets are
normalised first — centroid at the origin, mean distance √2 — and the normal equations of the
stacked rows are solved by Gaussian elimination with partial pivoting; a vanishing pivot is three
marks on one line. With more than four marks this is an algebraic least-squares fit. The marks
must be on one floor (within 5 cm), and spread out both in the picture and on the ground. The
sign of H is chosen so that the denominator w = h₇x + h₈y + h₉ is positive at every mark; a
point where w ≤ 0 is beyond the horizon, and projecting it answers nothing. The miss shown for each
mark is against the fit of all of them, in metres, for the reason given under the calibration
above. The browser receives the same H pre-multiplied by the frame's linear ENU→WGS84 map, a
3×3 matrix that turns (x, y, 1) into (lat·w, lon·w, w).

**A survey binding** (`PtzSlaveBindingMath`, the method `absolute`). Not a homography: the pair is
not two views of one plane, since the slave's pan and tilt are angles, not a picture. The marked
points are interpolated by a thin-plate spline — kernel U(r) = r²·ln r plus an affine part, pan and
tilt as two right-hand sides of one (n+3)×(n+3) system, regularised by λ = 10⁻³ so that a
single misplaced mark bends the sheet rather than tearing it. Three points make the sheet (an
affine map, then); *extrapolated* is outside the convex hull of the marks; the leave-one-out
miss is one Euclidean number over pan and tilt. The method `presets` fits nothing: it goes to the
preset of the nearest mark.

**The chain for `geo`** (`GeoAiming.Project`): the bottom middle of the master's box → the
master's homography → a point in the master's frame → WGS84 → the slave's frame → a bearing to
that point on the master's floor → the slave's two lines → a pan and tilt in the slave's units,
and the distance of that point → the zoom samples → a zoom. The command interval is the session's,
the same as for a survey; the dead zone is measured on the ground.

**Exact where, approximate where.** For an ideal pinhole camera over a flat floor and a level
PTZ with a linear scale, these models are exact: on the OnvifSim bench the PTZ fit's residual is
zero, the ground fit's 6 cm, and the head arrives within a thousandth of a degree of a point it
was sent to. A real camera departs from them through lens distortion (the ground mapping misses
more towards the edges of a wide frame), a mount out of level and a pan scale that is not quite
linear. The models do not correct any of that; they show it, in the residual columns, which is
why those columns are there.
