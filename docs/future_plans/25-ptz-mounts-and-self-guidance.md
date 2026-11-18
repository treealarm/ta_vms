# PTZ mounts and self-guidance: what the fit does not cover

> Found while writing [Calibration, aiming and the algorithms behind them](../maps/calibration.md).
> Two items: a fit that survives a leaning or sideways mount, and a self-guidance loop that
> fails safe when nothing is calibrated.

## Next stages

- **PTZ fit by mount orientation.** The two straight lines hold only for a level mount, angular
  units and an exact camera position. A camera on a wall bracket leans; one on a fence may hang on
  its side, so that pan sweeps up and down and tilt across; units may not be linear in angle; a
  placement a metre off skews near targets. Instead, staged by the number of observations:
  - 2–3: the two lines, as now — enough to aim at all.
  - 4+: a physical model fitted in one go by nonlinear least squares (Levenberg–Marquardt, with a
    loss that tolerates an outlier): the mount's **full** 3-D orientation — any rotation, not a
    small tilt, so sideways and upside-down mounts are the same case — plus a scale and an offset
    per axis in the camera's own units. Kabsch–Umeyama on the observed directions gives the
    starting rotation. (Kabsch alone was tried elsewhere and worked badly: it needs the units to be
    angles already, and an unknown scale turns into a wrong rotation.)
  - 6–8+: a smooth correction of the remaining misses across the covered sector, for units that
    are not linear in angle — trusted only where there are observations.
  - Optionally the camera's position too, when there are many well-spread landmarks.
  A richer model is chosen only when its held-out misses are actually smaller, so extra parameters
  do not fit noise. Decide which stages are needed from 8–10 observations on a real camera that
  takes absolute positions — none is on the bench yet. What the two lines cost today is measured
  on OnvifSim (`tests/onvifsim/README.md`, "What the mounts do to the aim"): a bracket leaning 10°
  forward stays within the thresholds, 8° sideways already misses them (1.7° mean, 2.7° p95), and
  a camera on its side is unusable (13° off, framed 12% of the time). Those two mounts are the
  test the new model has to pass.
- **Self-guidance over the mounts.** The bench measures only aiming through the map
  (`AbsoluteMove`). A camera that follows a person in its own picture is a different loop: the
  box's offset from the centre, times a 2×2 matrix, becomes a `ContinuousMove` velocity
  (`PtzTrackingMath`), and the matrix is measured in the picture by test pulses
  (`PtzCalibrationService`). That absorbs a reversed or swapped axis and a picture turned by a
  fixed angle by construction. What it cannot absorb is a mapping that changes along the travel —
  a leaning mount or one on its side, where the picture turns as the head pans, and zoom, which
  changes the gain — because the matrix is measured at one pose. And with no usable calibration
  (`|det| < 0.05`) it falls back to `(errorX, -errorY)`, the standard way round: on a camera with a
  reversed axis, or upside down with its picture left as it is, that is positive feedback and the
  head runs away from the person. Nothing checks either today. A bench step for it: give the
  slave its own analytics, run the automatic calibration (first finding out whether its motion
  estimate works on OnvifSim's flat-coloured surfaces), switch self-guidance on while a person
  walks, and measure the share of time in frame, the mean offset from the centre, overshoot and
  oscillation, and losses of the target — over the whole mount matrix, plus each mount with no
  calibration at all. If the uncalibrated fallback runs away, it should refuse to guide rather
  than guess.
- **A master whose picture is upside down.** The ground mapping takes a picture turned any way,
  but the aim point is the bottom of a person's box, which in a picture left upside down is the
  head: with marks clicked on the landmarks' feet the slave is then 27° off and never frames the
  person (bench mount `master-flip`). The fix belongs in the camera — turn its picture back — so
  the first step is to say so where the operator marks the ground, since nothing in the marks
  themselves gives the flip away. Teach the aim to use the box's top edge only if a camera turns
  up whose picture cannot be turned back.
- **A dead zone in degrees for a survey.** A survey slave's dead zone is 0.02 in the camera's own
  units. On a head whose generic range is a full turn, that is 3.6° of pan. That threshold, not the
  calibration, is what dominates how far behind a walking person it lags. (On the map method it is
  already measured on the ground.)
- **Aerial targets**: a master's full pose (PnP from landmarks with heights) gives a ray; a
  co-located slave follows its direction, two masters triangulate a 3D point.
- A footprint's colour from the state of the cameras inside it; walls that cut view cones.
