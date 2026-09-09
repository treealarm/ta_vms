using System.Threading.Channels;
using Vms.Analytics;

namespace VmsPtzTrackingSingleton;

/// <summary>
/// Keeps one camera pointed at one object.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately free of ambient state: sightings arrive through <see cref="Offer"/> and the only
/// way out is <see cref="IPtzCommandSender"/>, both handed in by the caller. Everything else is
/// private to the one task <see cref="RunAsync"/> owns, so commands to a camera are serialised
/// without a lock being needed for it.
/// </para>
/// <para>
/// The loop is pulsed rather than continuous. `analytics_watch.sample_fps` defaults to 1, and the
/// browser overlay measures the real spacing of CV samples at a median of 1.000 s — so a continuous
/// velocity would leave the camera driving open-loop for a whole second between corrections, which
/// overshoots and then oscillates. Instead each sighting buys one short burst of movement and a
/// stop, and the next sighting decides afresh. It also means a producer that goes quiet leaves the
/// camera stationary rather than running.
/// </para>
/// </remarks>
public sealed class TrackingSession
{
  // ── Control law ───────────────────────────────────────────────────────────
  //
  // The camera is not asked to centre the target. It is asked to keep the whole of it in frame,
  // which is a different job and the one an operator actually wants: a subject standing still and
  // fully visible is framed correctly already, and every command sent to improve on that is a
  // twitch of the picture bought for nothing. So the error driving the motor is not "how far off
  // centre" but "how far outside the frame" — zero for anything comfortably inside, and exactly
  // the shortfall for anything pressed against an edge.
  //
  // The two insets below are the hysteresis band, in geometry rather than in thresholds on a
  // distance. Nothing happens until the target comes within EdgeInset of an edge; once something
  // does happen, it runs until the target is SafeInset clear of it. The gap between them is what
  // a walking subject spends before asking for the next correction, and what stops a correction
  // that overshoots slightly from immediately provoking its opposite.

  /// <summary>How close to an edge the target may come before the camera is asked to act.</summary>
  private const float EdgeInset = 0.04f;

  /// <summary>How far inside the frame a correction puts the target once it has to run at all.</summary>
  private const float SafeInset = 0.12f;

  /// <summary>
  /// Extra clear space demanded above a person the detector has found no face on.
  /// </summary>
  /// <remarks>
  /// A person is worth watching for their head, and a shot of someone's legs with the head cropped
  /// away is a failure however well centred it is. The detector's box is the whole body, so keeping
  /// the box in frame keeps the head in frame — except when the head is <em>already</em> out, which
  /// is exactly when the box stops at the top edge and reports nothing above it.
  /// <para>
  /// A visible face settles the question: the head is in frame, nothing to do. Without one the
  /// camera gives a little upward room in case the head is cut off. The probe is self-limiting
  /// rather than a standing bias — it asks for clear space above the body and stops the moment
  /// there is some, so a person facing away, whose head is plainly in frame, is left alone.
  /// </para>
  /// </remarks>
  private const float HeadRoom = 0.08f;

  /// <summary>
  /// How far the target's centre may wander from the middle of the frame before the camera starts
  /// easing it back: half the frame, so a box of the middle 50% by 50%.
  /// </summary>
  /// <remarks>
  /// Keeping the target in frame is the requirement; keeping it near the middle is what makes that
  /// requirement easy to meet. The framing rule alone does nothing until the subject is against an
  /// edge and must then make one large, visible correction — and if the subject picks that moment
  /// to speed up, the correction is already late. This rule starts far earlier and asks for far
  /// less. There is no danger of the picture flying about, because the size of a correction is the
  /// size of the excess, and at the moment this triggers the excess is nearly nothing: through the
  /// speed law below it comes out barely above the motor's breakaway speed, growing only as the
  /// subject gets further out.
  /// </remarks>
  private const float DriftZone = 0.25f;

  /// <summary>How close to the middle an easing correction brings it, once one is running.</summary>
  /// <remarks>
  /// The gap to <see cref="DriftZone"/> is a hysteresis band, the same idea as the gap between
  /// <see cref="EdgeInset"/> and <see cref="SafeInset"/>: without it a subject loitering exactly on
  /// the boundary would earn a command on every sighting.
  /// </remarks>
  private const float DriftSettle = 0.20f;

  private const float Gain = 1.2f;
  /// Below this a PTZ motor commonly does not break away at all, so a smaller command is no command.
  private const float MinSpeed = 0.08f;
  /// Past this the picture smears while panning and the detector loses the object it is chasing.
  private const float MaxSpeed = 0.50f;

  // ── Learning how far this camera actually moves ───────────────────────────
  /// <summary>
  /// Multiplier on the pulse length, learned per session from the camera's own behaviour.
  /// </summary>
  /// <remarks>
  /// <para>
  /// How far a speed command shifts the picture depends on the head's top speed and the lens, and
  /// nothing tells us either. Guessing badly is what makes a camera hunt: guess too high and every
  /// correction sails past the target, and the picture swings to and fro without ever settling.
  /// </para>
  /// <para>
  /// So it is measured instead of assumed. Overshoot leaves an unmistakable trace — the error
  /// vector comes back pointing the opposite way — and no progress leaves the error much as it
  /// was. Shrinking on the first and growing on the second walks the scale onto whatever this
  /// camera really does, within a few pulses and without any per-camera configuration.
  /// </para>
  /// <para>
  /// Not persisted: it costs a handful of pulses to relearn, and a stored value would be wrong the
  /// moment the camera is zoomed — the same command covers far less of a narrow field of view.
  /// </para>
  /// </remarks>
  private const float StepScaleMin = 0.15f;
  private const float StepScaleMax = 2.0f;
  private const float StepShrink = 0.55f;
  private const float StepGrow = 1.35f;
  /// An error still this close to what it was means the pulse barely moved the camera.
  private const float NoProgressRatio = 0.85f;

  /// <summary>
  /// How long a burst of movement lasts, for a target far enough off centre to earn a full one.
  /// </summary>
  /// <remarks>
  /// Scaled down for smaller errors by <see cref="PulseFor"/>. It has to be: speed alone cannot
  /// make a small correction, because <see cref="MinSpeed"/> is a floor below which the motor does
  /// not break away at all. With a fixed duration the smallest possible move was therefore a
  /// constant, whatever the error — and a constant step larger than the error left is exactly what
  /// overshoots, which is what the hysteresis band above then has to absorb.
  /// </remarks>
  private static readonly TimeSpan MaxPulse = TimeSpan.FromMilliseconds(300);
  /// Short enough to be a nudge, long enough that the motor actually starts.
  private static readonly TimeSpan MinPulse = TimeSpan.FromMilliseconds(80);
  /// The error that earns a full-length pulse; anything larger is clamped to one.
  private const float FullPulseError = 0.25f;
  private static readonly TimeSpan MinCommandInterval = TimeSpan.FromMilliseconds(200);
  /// Backstop for a Stop that failed: the camera's own PT3S timeout is the outer one.
  private static readonly TimeSpan SafetyStop = TimeSpan.FromMilliseconds(2500);
  private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);
  // Detector outputs while the scene is still slewing under PTZ are poor reacquire signals: the
  // model sees motion blur and parallax rather than the same stable object. Wait a short moment
  // after each commanded pulse before trusting new boxes again.
  private static readonly TimeSpan IgnoreDetectionsAfterMove = TimeSpan.FromMilliseconds(800);

  // ── Target handling ───────────────────────────────────────────────────────

  /// <summary>
  /// How long the target must be missing before another box may be taken for it.
  /// </summary>
  /// <remarks>
  /// Longer than one sample interval on purpose. A target absent from a single frame — briefly
  /// occluded, or just missed — has not gone anywhere, and reacquiring immediately would hand the
  /// camera to whoever happened to be standing nearby.
  /// </remarks>
  private static readonly TimeSpan ReacquireAfter = TimeSpan.FromMilliseconds(1200);
  /// Normalised distance from the target's last known centre within which a box may be it.
  private const float ReacquireRadius = 0.25f;
  private static readonly TimeSpan Lost = TimeSpan.FromSeconds(5);

  private readonly string _cameraId;
  private readonly IPtzCommandSender _sender;
  private readonly ILogger _logger;
  private PtzCalibrationModel? _calibration;
  private TrackingPolicy _policy;

  // Sightings from the shared detection stream. Bounded and drop-oldest for the same reason
  // LiveDetectionBroadcaster is: a stale sighting is worthless to a control loop, and the pump
  // feeding every session must never block on one of them.
  private readonly Channel<DetectionSample> _samples = Channel.CreateBounded<DetectionSample>(
    new BoundedChannelOptions(256)
    {
      FullMode = BoundedChannelFullMode.DropOldest,
      SingleReader = true,
      SingleWriter = false,
    });

  // Guards only what crosses the task boundary: SetTarget arrives on a gRPC thread and Snapshot is
  // read by another. The control law itself never leaves the loop.
  private readonly Lock _gate = new();
  private TargetKey? _target;
  private bool _explicitTarget;
  private DateTime? _targetSeenAt;
  private string _lastError = string.Empty;
  private float _lastX = 0.5f;
  private float _lastY = 0.5f;
  private string? _lastObjectId;

  private bool _moving;
  /// <summary>The target is framed well enough to be left alone; see <see cref="EdgeInset"/>.</summary>
  /// <remarks>
  /// Starts false so the very first sighting is seated properly rather than being accepted wherever
  /// it happens to appear.
  /// </remarks>
  private bool _centred;
  private float _stepScale = 1f;
  /// The error vector as it stood when the last command went out; null when none has.
  private (float X, float Y)? _errorAtLastCommand;
  private DateTime _stopAt;
  private DateTime _lastCommandAt = DateTime.MinValue;
  private DateTime _ignoreDetectionsUntil = DateTime.MinValue;

  public TrackingSession(
    string cameraId,
    IPtzCommandSender sender,
    ILogger logger,
    TargetKey? target,
    PtzCalibrationModel? calibration,
    TrackingPolicy policy)
  {
    _cameraId = cameraId;
    _sender = sender;
    _logger = logger;
    _calibration = calibration;
    _policy = policy;
    _target = target;
    _explicitTarget = target is not null;
    // A freshly named target has not been seen by this session yet, but it must not look lost
    // before its first sighting arrives either.
    _targetSeenAt = target is null ? null : DateTime.UtcNow;
    _logger.LogInformation(
      "Camera '{Camera}': tracking session started, following {Policy}, calibration {Calibration}",
      _cameraId, Describe(policy), Describe(calibration));
  }

  public void Offer(DetectionSample sample) => _samples.Writer.TryWrite(sample);

  public void SetTarget(TargetKey? target)
  {
    lock (_gate)
    {
      if (target is null)
      {
        // Handing the camera back: it resumes choosing for itself on the next sighting.
        ForgetTargetLocked();
      }
      else
      {
        _target = target;
        _explicitTarget = true;
        _targetSeenAt = DateTime.UtcNow;
        _lastObjectId = null;
      }
    }
    ForgetObservation();
  }

  public void ApplyCalibration(PtzCalibrationModel? calibration)
  {
    // Reconciles run on a timer and on every camera change in the deployment, so the great majority
    // of these calls hand over exactly what is already held. Announcing those would bury the ones
    // that mean something.
    string? announce = null;
    lock (_gate)
    {
      if (Describe(_calibration) != Describe(calibration))
        announce = Describe(calibration);
      _calibration = calibration;
    }
    if (announce is not null)
      _logger.LogInformation("Camera '{Camera}': calibration is now {Calibration}", _cameraId, announce);
  }

  /// <summary>
  /// Takes a changed policy without interrupting what the camera is doing.
  /// </summary>
  /// <remarks>
  /// An administrator may reorder the priorities while a camera is mid-chase, and the object being
  /// followed is not wrong merely because something else is now preferred — the new order decides
  /// the <em>next</em> choice. What does end immediately is following a source that has just been
  /// switched off, since nothing will renew that target and it would be held until it timed out.
  /// </remarks>
  public void ApplyPolicy(TrackingPolicy policy)
  {
    var dropped = false;
    var changed = false;
    lock (_gate)
    {
      changed = Describe(_policy) != Describe(policy)
        || !_policy.Sources.SetEquals(policy.Sources);
      _policy = policy;
      if (_target is not null && !policy.Accepts(_target.Source))
      {
        _logger.LogInformation(
          "Camera '{Camera}': dropping the target, source '{Source}' is no longer followed",
          _cameraId, _target.Source);
        ForgetTargetLocked();
        dropped = true;
      }
    }
    if (dropped)
      ForgetObservation();
    if (changed)
      _logger.LogInformation("Camera '{Camera}': now following {Policy}", _cameraId, Describe(policy));
  }

  /// <summary>Drops everything known about the current target.</summary>
  /// <remarks>
  /// Call under <see cref="_gate"/>. Only the target bookkeeping, which is what the gate exists to
  /// protect — the half-finished observation of where the camera had got to (<see cref="_centred"/>,
  /// <see cref="_errorAtLastCommand"/>) belongs to the control loop, and every caller clears it
  /// outside the lock through <see cref="ForgetObservation"/>.
  /// </remarks>
  private void ForgetTargetLocked()
  {
    _target = null;
    _explicitTarget = false;
    _targetSeenAt = null;
    _lastObjectId = null;
    _lastX = 0.5f;
    _lastY = 0.5f;
  }

  /// <summary>
  /// Forgets where the camera had got to with the previous target.
  /// </summary>
  /// <remarks>
  /// A new object is a fresh job: it is seated properly rather than accepted wherever it happens to
  /// stand. The learned step scale survives on purpose — it describes the camera, not the target.
  /// </remarks>
  private void ForgetObservation()
  {
    _centred = false;
    _errorAtLastCommand = null;
  }

  private static string Describe(TrackingPolicy policy) =>
    policy.Kinds.Count == 0
      ? "any object"
      : string.Join(" then ", policy.Kinds.Select(k => k.ToString().ToLowerInvariant()));

  /// <summary>
  /// The calibration as the control law reads it, for the log.
  /// </summary>
  /// <remarks>
  /// Each vector is where the picture's content goes for a positive command on that axis, so a
  /// correctly mounted camera reads pan=(1,0) tilt=(0,1): tilting up slides the scene down, and
  /// image y grows downward. A tilt of (0,-1) is the classic wrong answer — it makes the solver
  /// drive the camera away from a target below the centre — which is why the sign is spelled out
  /// here rather than left to be inferred from four numbers.
  /// </remarks>
  private static string Describe(PtzCalibrationModel? c) =>
    c is null
      ? "none (using the uncalibrated fallback)"
      : $"pan=({c.PanDx:F2},{c.PanDy:F2}) tilt=({c.TiltDx:F2},{c.TiltDy:F2}) "
        + $"det={(c.PanDx * c.TiltDy) - (c.PanDy * c.TiltDx):F3}";

  public TrackingSnapshot Snapshot()
  {
    lock (_gate)
      return new TrackingSnapshot(
        _cameraId, true, _target, _explicitTarget, _targetSeenAt, _lastError);
  }

  public async Task RunAsync(CancellationToken ct)
  {
    try
    {
      while (!ct.IsCancellationRequested)
      {
        var batch = await ReadBatchAsync(ct);
        if (batch.Count > 0)
          await OnBatchAsync(batch, ct);
        await TickAsync(ct);
      }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Tracking loop for camera '{Camera}' stopped", _cameraId);
    }
    finally
    {
      // Whatever ended this loop — cancellation, a bug — the camera must not be left running. The
      // camera's own PT3S timeout would eventually stop it, but three seconds of unattended pan is
      // three seconds pointing somewhere nobody asked for.
      await SafeStopAsync(CancellationToken.None);
    }
  }

  /// <summary>
  /// Every sighting already queued, or none once <see cref="Tick"/> has passed.
  /// </summary>
  /// <remarks>
  /// Drained as a batch because the producers emit one message per detection: the objects of a
  /// single video frame arrive as several samples, and reacquiring by proximity has to weigh them
  /// against each other rather than against whichever one was read first.
  /// </remarks>
  private async Task<List<DetectionSample>> ReadBatchAsync(CancellationToken ct)
  {
    var batch = new List<DetectionSample>();
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(Tick);
    try
    {
      if (await _samples.Reader.WaitToReadAsync(timeout.Token))
        while (_samples.Reader.TryRead(out var sample))
          batch.Add(sample);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
      // The tick elapsed with nothing to read — an ordinary quiet moment, not a failure.
    }
    return batch;
  }

  private async Task OnBatchAsync(List<DetectionSample> batch, CancellationToken ct)
  {
    var match = Match(batch);
    if (match is null)
      return;

    lock (_gate)
    {
      // A reacquired box is the same object under a new track id, so the key follows it; whether
      // the operator chose it in the first place is unchanged by that.
      _target = match.Key;
      _targetSeenAt = DateTime.UtcNow;
      _lastX = match.CenterX;
      _lastY = match.CenterY;
      if (match.ObjectId is not null)
        _lastObjectId = match.ObjectId;
    }

    await DriveAsync(match, FaceVisible(match, batch), ct);
  }

  /// <summary>
  /// Whether the detector can see the face of the body being tracked.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The face is used as evidence about the head rather than as something to aim at. Aiming at it
  /// was the obvious first idea and is the wrong one: faces come and go with head pose, so the aim
  /// point would jump between face and torso every time the subject turned round, and each jump is
  /// a command to the motor. What the operator wants is not the face in the middle of the picture
  /// but the head in the picture at all — and for that, one bit is enough. See
  /// <see cref="HeadRoom"/>.
  /// </para>
  /// <para>
  /// The link between a face and its body is the track id: the detector gives a face the track id
  /// of the person it was found on (`video_a/inference/pipeline.cpp` — "Face/plate detections share
  /// a parent track_id"), which is a statement from the model itself and better than any
  /// box-overlap rule this side could invent. An unparented face carries track id 0 and so belongs
  /// to nobody.
  /// </para>
  /// </remarks>
  private static bool FaceVisible(DetectionSample target, List<DetectionSample> batch)
  {
    if (target.Kind != DetectionKind.Person || target.TrackId == 0)
      return false;

    foreach (var sample in batch)
      if (sample.Kind == DetectionKind.Face
        && sample.TrackId == target.TrackId
        && sample.Source == target.Source)
        return true;

    return false;
  }

  /// <summary>
  /// How far the target must move for the whole of it to sit inside the frame, with
  /// <paramref name="inset"/> of clear space on every side.
  /// </summary>
  /// <returns>
  /// The movement the picture's content must make, in frame fractions; (0,0) when the target is
  /// already seated, which is the ordinary answer and the one that keeps the camera still.
  /// </returns>
  /// <remarks>
  /// <para>
  /// A person's vertical axis is one-sided: it asks the camera to tilt up to bring the head in, and
  /// never to tilt down on account of the feet. That is not a preference but the resolution of a
  /// contradiction. A tall subject cannot be given room above their head <em>and</em> keep their
  /// feet in shot — the two demands add up to more than a frame — so as two constraints they simply
  /// disagree, and the answer turned on whichever was evaluated. Through <see cref="HeadRoom"/>
  /// that meant it turned on whether the detector had found a face in that particular frame; faces
  /// come and go several times a second, so the camera was handed "show the head" and "show the
  /// feet" alternately and see-sawed between them, exactly where the arithmetic says it must — a
  /// body filling most of the frame's height, with no face to settle the question.
  /// </para>
  /// <para>
  /// There is no stable signal with which to choose between the two, so the fix is for there to be
  /// only one: a person is worth watching for their head, so the head is what must be in frame and
  /// the feet are what get cropped. The flickering face can then no longer reverse anything — at
  /// worst it changes how much room is asked for above, which the hysteresis band absorbs.
  /// </para>
  /// <para>
  /// A person genuinely too low in the picture is still corrected, by the centring rule: their
  /// box's centre leaves <see cref="DriftZone"/> long before they leave the frame. This is about
  /// which edge wins when both cannot, not about ignoring half the picture.
  /// </para>
  /// </remarks>
  private static (float X, float Y) FramingError(
    DetectionSample target, bool wantsHeadRoom, float inset)
  {
    var above = inset + (wantsHeadRoom ? HeadRoom : 0f);
    var headFirst = target.Kind == DetectionKind.Person;
    // Whether the subject fits is decided once, at the goal's insets, and the same answer is used
    // for the threshold that wakes the camera. Letting each work it out from its own insets meant
    // they could disagree — a box between the two widths was "too big to seat" for the goal, and so
    // asked for nothing, while still counting as over the edge for the threshold. The camera then
    // had a reason to act and nothing to do, and flipped between settled and not on every sighting.
    return (
      Overflow(target.Left, target.Width, inset, inset,
        twoSided: Fits(target.Width, SafeInset, SafeInset)),
      Overflow(target.Top, target.Height, above, inset,
        twoSided: !headFirst && Fits(target.Height, SafeInset + (wantsHeadRoom ? HeadRoom : 0f), SafeInset)));
  }

  /// <summary>Whether a span of this size can sit between the two insets at all.</summary>
  private static bool Fits(float size, float before, float after) => size <= 1f - before - after;

  /// <summary>
  /// How far the target's centre must move to be back inside the middle of the frame.
  /// </summary>
  /// <returns>
  /// The movement the picture's content must make, in the same sense as
  /// <see cref="FramingError"/>; (0,0) while the centre is inside the zone, which is the ordinary
  /// answer and the one that keeps the camera still.
  /// </returns>
  private static (float X, float Y) CentringError(DetectionSample target, float zone) =>
    (Beyond(target.CenterX - 0.5f, zone), Beyond(target.CenterY - 0.5f, zone));

  /// <summary>The part of an offset from centre that lies outside the zone, as a correction.</summary>
  private static float Beyond(float offset, float zone)
  {
    if (offset > zone)
      return -(offset - zone);
    if (offset < -zone)
      return -(offset + zone);
    return 0f;
  }

  /// <summary>Whichever of two corrections along one axis asks for more.</summary>
  /// <remarks>
  /// The two rules never disagree about direction — a subject off to the right is off to the right
  /// whether it was the frame's edge or the middle of the picture that noticed — so the larger
  /// demand simply satisfies both.
  /// </remarks>
  private static float Larger(float a, float b) => MathF.Abs(a) >= MathF.Abs(b) ? a : b;

  /// <summary>
  /// How far a span must slide along one axis to lie between the two insets.
  /// </summary>
  /// <param name="twoSided">
  /// Whether running past the far edge is worth correcting. False means only the near edge counts —
  /// see <see cref="FramingError"/> for why a person's vertical axis is one-sided.
  /// </param>
  /// <remarks>
  /// Zero whenever it already does — the whole point of the exercise. A span too long to fit
  /// between them is held against the near edge instead. Even then the answer is zero once the near
  /// edge is clear, and deliberately so: an object that cannot be framed whole must not become a
  /// target the camera can never reach and so corrects towards for ever.
  /// </remarks>
  private static float Overflow(float start, float size, float before, float after, bool twoSided)
  {
    var shortfall = before - start;
    if (!twoSided)
      return MathF.Max(shortfall, 0f);
    if (shortfall > 0f)
      return shortfall;
    var over = (start + size) - (1f - after);
    return over > 0f ? -over : 0f;
  }


  private DetectionSample? Match(List<DetectionSample> batch)
  {
    TargetKey? target;
    DateTime? seenAt;
    float lastX, lastY;
    string? lastObjectId;
    bool explicitTarget;
    TrackingPolicy policy;
    lock (_gate)
    {
      target = _target;
      seenAt = _targetSeenAt;
      lastX = _lastX;
      lastY = _lastY;
      lastObjectId = _lastObjectId;
      explicitTarget = _explicitTarget;
      policy = _policy;
    }

    // Strip oversized boxes before any matching — they are either very-close-range objects the
    // camera cannot usefully track, or YOLO false positives on background texture/fabric.
    // A box from a producer this camera is not configured to follow is not a candidate for
    // anything — not for a fresh target, and not for reacquiring one either.
    var usable = TargetChooser.Usable(batch, policy);
    if (usable.Count == 0)
      return null;

    // An exact key match is the current target still being reported — trust it even while the
    // camera is settling. Everything else waits: a neighbour or a first-come box during a slew
    // is how tracking latches onto background texture.
    if (target is not null)
    {
      foreach (var sample in usable)
        if (sample.Key == target)
          return sample;
    }

    if (DateTime.UtcNow < _ignoreDetectionsUntil)
      return null;

    if (target is null)
      return TargetChooser.Choose(usable, policy);

    // Present a moment ago — wait for it rather than grabbing a neighbour.
    if (seenAt is not null && DateTime.UtcNow - seenAt.Value < ReacquireAfter)
      return null;

    // Re-identification first: it is the one signal that means "the same physical object" across a
    // change of track id, which is what a moving camera causes.
    if (lastObjectId is not null)
      foreach (var sample in usable)
        if (sample.ObjectId == lastObjectId)
          return sample;

    // A target the operator chose explicitly must not jump to a neighbour merely because the whole
    // scene moved under the detector while the camera was panning. Without a stable object id,
    // "nearest to where it used to be" is how tracking latches onto a bystander or background box.
    if (explicitTarget)
      return null;

    DetectionSample? nearest = null;
    var nearestDistance = ReacquireRadius;
    foreach (var sample in usable)
    {
      if (sample.Kind != target.Kind)
        continue;
      var dx = sample.CenterX - lastX;
      var dy = sample.CenterY - lastY;
      var distance = MathF.Sqrt((dx * dx) + (dy * dy));
      if (distance < nearestDistance)
      {
        nearestDistance = distance;
        nearest = sample;
      }
    }
    return nearest;
  }

  /// Speed proportional to how far off centre the object is, with a floor that actually moves the
  /// motor and a ceiling that keeps the picture sharp enough to keep detecting.
  /// The dead zone is applied in image space by the caller — applying it here after the
  /// calibration transform would shrink it when |det| is small and hunt on a centred target.
  private static float Velocity(float command, float factor)
  {
    if (command == 0f)
      return 0f;
    var speed = Math.Clamp(
      (MinSpeed + (Gain * MathF.Abs(command))) * factor, MinSpeed, MaxSpeed);
    return MathF.Sign(command) * speed;
  }

  /// <summary>
  /// How small a move to make, given how far off centre the target is and what this camera has been
  /// observed to do with such a command.
  /// </summary>
  /// <remarks>
  /// The learned scale is a demand for a smaller movement, and there are two ways to grant it.
  /// Duration answers first, because a shorter burst is the honest way to move less. But duration
  /// has a floor — below <see cref="MinPulse"/> the head does not respond at all — and on a camera
  /// with a fast head or a long lens that floor still carries the picture clean across the
  /// hysteresis band, which leaves it swinging with nothing left to shorten. So the shortfall is
  /// taken off the speed instead, down to <see cref="MinSpeed"/>, where the motor's own breakaway
  /// threshold ends the argument.
  /// </remarks>
  private (TimeSpan Pulse, float SpeedFactor) StepFor(float error)
  {
    var share = Math.Clamp(error / FullPulseError, 0f, 1f);
    var pulse = (MinPulse + ((MaxPulse - MinPulse) * share)) * _stepScale;
    if (pulse >= MinPulse)
      return (pulse, 1f);
    return (MinPulse, (float)(pulse / MinPulse));
  }

  /// <summary>
  /// Reads the last pulse's result and adjusts <see cref="_stepScale"/> accordingly.
  /// </summary>
  /// <remarks>
  /// Overshoot is judged by the error vector reversing — a negative dot product with where it
  /// pointed when the command went out. That is the two-dimensional form of "the correction went
  /// past the target", and unlike comparing magnitudes it cannot be fooled by a target that simply
  /// moved further away while the camera was busy.
  /// </remarks>
  private PulseResult LearnStepScale(float errorX, float errorY, float error)
  {
    if (_errorAtLastCommand is not { } prev)
      return PulseResult.Unknown;
    _errorAtLastCommand = null;

    var previous = MathF.Sqrt((prev.X * prev.X) + (prev.Y * prev.Y));
    if (previous <= 0f)
      return PulseResult.Unknown;

    var reversed = ((errorX * prev.X) + (errorY * prev.Y)) < 0f;
    // A framing error of zero means the target is seated where it was wanted, however it got
    // there — that is a correction that worked, not one that went too far.
    if (reversed && error > 0f)
      _stepScale = MathF.Max(StepScaleMin, _stepScale * StepShrink);
    else if (!reversed && error > previous * NoProgressRatio)
      _stepScale = MathF.Min(StepScaleMax, _stepScale * StepGrow);
    else
      return reversed ? PulseResult.Overshot : PulseResult.Closed;

    _logger.LogDebug(
      "Camera '{Camera}': {What}, step scale now {Scale:F2}",
      _cameraId, reversed ? "overshot the target" : "barely moved", _stepScale);
    return reversed ? PulseResult.Overshot : PulseResult.BarelyMoved;
  }

  /// <summary>What the last commanded pulse turned out to have done.</summary>
  private enum PulseResult
  {
    /// No pulse to judge — the first sighting of a target, or one arriving between corrections.
    Unknown,
    /// The error vector reversed: the correction carried the picture past the target.
    Overshot,
    /// The error hardly changed, so the command moved the head less than it was meant to.
    BarelyMoved,
    /// The correction closed on the target without passing it.
    Closed,
  }

  private async Task DriveAsync(DetectionSample target, bool faceVisible, CancellationToken ct)
  {
    // A sighting taken while the camera is still moving describes neither where the target was nor
    // where it will be. Match() lets the current target through this window on purpose — losing
    // track of it would be worse — but the control law must not act on it: judging the last pulse
    // by it reads "barely moved" from a move that has not finished, which walks the step scale up
    // to its ceiling and doubles every pulse thereafter. At the sample rate this feature asks for
    // (5 fps) that is most of the sightings, not an edge case.
    if (DateTime.UtcNow < _ignoreDetectionsUntil)
      return;

    var wantsHeadRoom = target.Kind == DetectionKind.Person && !faceVisible;

    // Two rules, each with a threshold for noticing and a goal for once it has noticed.
    //
    // The framing rule cares that all of the subject is in the picture, and speaks up only when
    // some of it is about to leave. The centring rule cares that the subject is drifting towards an
    // edge at all, and speaks up much earlier and much more quietly.
    var atEdge = FramingError(target, wantsHeadRoom, EdgeInset);
    var seat = FramingError(target, wantsHeadRoom, SafeInset);
    var drifted = CentringError(target, DriftZone);
    var recentre = CentringError(target, DriftSettle);

    // Either rule can wake the camera; whichever then asks for more decides how far it goes.
    var wanted = atEdge is not (0f, 0f) || drifted is not (0f, 0f);
    var goalX = Larger(seat.X, recentre.X);
    var goalY = Larger(seat.Y, recentre.Y);

    // The picture must move one way, so the camera must turn the other: an object pressed against
    // the left edge has to slide right, which is a leftward pan.
    var errorX = -goalX;
    var errorY = -goalY;
    var error = MathF.Sqrt((errorX * errorX) + (errorY * errorY));
    var pulse = LearnStepScale(errorX, errorY, error);

    if (_centred)
    {
      if (!wanted)
      {
        await SafeStopAsync(ct);
        return;
      }
      _centred = false;
      _logger.LogDebug(
        "Camera '{Camera}': target {Why}, correcting", _cameraId,
        atEdge is (0f, 0f) ? "has drifted off centre" : "has reached the edge of frame");
    }
    // Settled either by being seated where it was wanted, or by the camera having demonstrated it
    // cannot do better: a correction that reversed the error while the target is no longer at any
    // edge has just shown the remainder to be smaller than this camera's smallest possible move.
    // Correcting again only hops back across, and that is the swinging an operator sees while a
    // motionless subject is chased.
    else if ((goalX == 0f && goalY == 0f) || (pulse == PulseResult.Overshot && !wanted))
    {
      _centred = true;
      _logger.LogDebug(
        "Camera '{Camera}': target {How}, holding", _cameraId,
        goalX == 0f && goalY == 0f
          ? "framed and near enough the middle"
          : "as well framed as this camera can put it");
      await SafeStopAsync(ct);
      return;
    }

    float panInput;
    float tiltInput;
    PtzCalibrationModel? calibration;
    lock (_gate)
      calibration = _calibration;
    if (calibration is not null)
      (panInput, tiltInput) = PtzTrackingMath.ImageErrorToCommand(errorX, errorY, calibration);
    else
    {
      panInput = errorX;
      // The overlay's y grows downward and ONVIF's tilt is positive upward.
      tiltInput = -errorY;
    }

    var step = StepFor(error);
    var pan = Velocity(panInput, step.SpeedFactor);
    var tilt = Velocity(tiltInput, step.SpeedFactor);

    if (pan == 0f && tilt == 0f)
    {
      await SafeStopAsync(ct);
      return;
    }

    var now = DateTime.UtcNow;
    if (now - _lastCommandAt < MinCommandInterval)
      return;

    // One line per command, which is at most five a second and in practice about one — the
    // detector's own rate. It carries the whole decision because every part of it has been
    // suspected at some point: where the object is, how far off centre that is, which calibration
    // was applied, and what the solver made of the two. Reading it against the rule in Describe()
    // is how a wrong calibration is told from a wrong control law.
    _logger.LogDebug(
      "Camera '{Camera}': {Kind} box ({Bx:F3},{By:F3} {Bw:F3}x{Bh:F3}){HeadRoom} "
      + "error=({Ex:+0.000;-0.000},{Ey:+0.000;-0.000}) |error|={Error:F3} {Calibration} -> "
      + "input=({PanIn:+0.000;-0.000},{TiltIn:+0.000;-0.000}) "
      + "command=(pan {Pan:+0.00;-0.00}, tilt {Tilt:+0.00;-0.00}) for {PulseMs}ms "
      + "(step scale {Scale:F2})",
      _cameraId, target.Kind, target.Left, target.Top, target.Width, target.Height,
      wantsHeadRoom ? ", no face — asking for head room" : string.Empty,
      errorX, errorY, error, Describe(calibration),
      panInput, tiltInput, pan, tilt, (int)step.Pulse.TotalMilliseconds, _stepScale);

    RecordError(await _sender.MoveAsync(_cameraId, pan, tilt, ct));
    _lastCommandAt = now;
    _moving = true;
    _stopAt = now + step.Pulse;
    _errorAtLastCommand = (errorX, errorY);
    _ignoreDetectionsUntil = _stopAt + IgnoreDetectionsAfterMove;
  }

  private async Task TickAsync(CancellationToken ct)
  {
    var now = DateTime.UtcNow;

    if (_moving && (now >= _stopAt || now - _lastCommandAt > SafetyStop))
      await SafeStopAsync(ct);

    TargetKey? target;
    DateTime? seenAt;
    bool explicitTarget;
    lock (_gate)
    {
      target = _target;
      seenAt = _targetSeenAt;
      explicitTarget = _explicitTarget;
    }

    if (target is null || seenAt is null || now - seenAt.Value <= Lost)
      return;

    await SafeStopAsync(ct);
    // Losing the target ends the operator's override as surely as it ends an automatic choice.
    //
    // That is what keeps the override from being permanent. Nothing switches the camera back to its
    // own priorities on a timer, and nobody is asked to press anything when they walk away from the
    // monitor — so a camera left pinned to an object that has gone would sit waiting for it to
    // return, and on an unattended wall would wait for ever. Instead the object going is the end of
    // the errand, and the camera resumes doing what it was configured to do.
    lock (_gate)
      ForgetTargetLocked();
    ForgetObservation();

    _logger.LogInformation(
      explicitTarget
        ? "Camera '{Camera}': the operator's target is gone, going back to the camera's own priorities"
        : "Camera '{Camera}': target lost, waiting for the next object",
      _cameraId);
  }

  private async Task SafeStopAsync(CancellationToken ct)
  {
    if (!_moving)
      return;
    _moving = false;
    RecordError(await _sender.StopAsync(_cameraId, ct));
  }

  private void RecordError(string? error)
  {
    lock (_gate)
      _lastError = error ?? string.Empty;
  }
}
