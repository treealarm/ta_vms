using Vms.Cfg;
using System.Threading.Channels;
using VmsPtzTrackingSingleton.Geo;

namespace VmsPtzTrackingSingleton;

/// <summary>
/// Points a PTZ camera at objects a different, stationary camera can see.
/// </summary>
/// <remarks>
/// <para>
/// The second aiming law, beside <see cref="TrackingSession"/>, and deliberately a separate class:
/// the two share only where their detections come from. A self-guiding camera closes a loop on its
/// own picture -- it nudges, looks at what moved, and nudges again -- and every constant in that
/// law exists to keep the loop from oscillating. This one has no loop to close. The slave usually
/// cannot see what it is being pointed at until it arrives, and when it does arrive nothing tells
/// it whether it arrived correctly. So the position is computed from the master's picture and
/// commanded outright, and everything that would have been feedback is a property of the binding
/// instead.
/// </para>
/// <para>
/// What replaces the loop's damping is a dead zone on the output and a floor on how often a command
/// may go out. A slew takes time the camera spends looking at nowhere in particular, so re-aiming
/// at every detection of a person shifting their weight would leave the camera permanently in
/// transit and permanently useless. The object has to have actually gone somewhere.
/// </para>
/// <para>
/// The aiming point is the bottom edge of the object's box, not its middle, and that is not a
/// framing preference. A point in the master's frame means "where this ray meets the ground", which
/// is the assumption the whole binding rests on; feet are on the ground and a chest is not, so a
/// ray through the middle of a person lands well beyond them -- metres, at any useful distance.
/// The cost of aiming low is that the slave looks at feet rather than a chest, a few degrees, which
/// the operator cancels at survey time by marking each point a person's height above the spot.
/// </para>
/// <para>
/// A binding on the map method has no survey: the same foot goes through the master's ground
/// calibration and the slave's landmark calibration instead. Which of the two applies is the
/// business of <see cref="SlaveAim"/>, which the session is handed whole and the reconcile replaces
/// whole.
/// </para>
/// </remarks>
public sealed class SlaveAimingSession
{
  /// <summary>How long to wait for detections before looking at the clock again.</summary>
  private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(200);

  /// <summary>
  /// How far the computed position must move before the camera is sent anywhere.
  /// </summary>
  /// <remarks>
  /// In the camera's own position units, so on a normalised camera this is 2% of full travel. The
  /// object's box jitters by a pixel or two between frames even when nothing moves, and every one
  /// of those would otherwise be a slew.
  /// </remarks>
  private const float MoveThreshold = 0.02f;

  /// <summary>
  /// How far the object must move on the ground before the camera is sent anywhere, on the map
  /// method.
  /// </summary>
  /// <remarks>
  /// <para>
  /// In metres rather than in the camera's units, because the map method zooms. A zoomed slave's
  /// whole frame can be narrower than <see cref="MoveThreshold"/>, which on a head with a full turn
  /// of travel is 3.6° of pan -- the object would leave the picture before the dead zone let the
  /// camera follow it. The frame the operator sampled is a stretch of ground around the object
  /// whatever the distance, so a stretch of ground is what the threshold has to be too.
  /// </para>
  /// <para>
  /// Half a metre is the floor, and well below any frame worth sampling, which has to fit a person
  /// and a couple of seconds of their walk. It is raised to what the master's box jitter amounts to
  /// on the ground at the object (<see cref="PositionAim.GroundToleranceM"/>): a pixel is a few
  /// centimetres near the master but grows with the square of the distance, past a metre at 70 m
  /// from a master 5 m up, and a fixed threshold there would re-aim at a person standing still.
  /// </para>
  /// <para>
  /// Only for a slave that zooms. One that does not keeps <see cref="MoveThreshold"/>: its frame is
  /// the wide end, which that threshold suits, and which the ground gains it nothing over.
  /// </para>
  /// </remarks>
  private const double GroundMoveThresholdM = 0.5;

  /// <summary>
  /// How far the zoom must change for that alone to be worth a command, in the camera's units.
  /// </summary>
  /// <remarks>
  /// The zoom follows the object's distance, so the ground threshold covers it while the object
  /// moves; this is for the samples themselves changing under a standing object.
  /// </remarks>
  private const float ZoomThreshold = 0.02f;

  /// <summary>The least time between two commands, however far the object has gone.</summary>
  /// <remarks>
  /// A camera that is still travelling to the last place it was sent cannot usefully be sent
  /// somewhere else; on the presets method it will not even acknowledge it. This is the open-loop
  /// stand-in for waiting on MoveStatus, which the cameras that need this method do not report
  /// truthfully.
  /// </remarks>
  private static readonly TimeSpan MinCommandInterval = TimeSpan.FromSeconds(2);

  /// <summary>How long an unseen object still counts as the thing being followed.</summary>
  /// <remarks>
  /// Longer than the self-guiding law's equivalent. There is no picture to reacquire from here, so
  /// the only cost of holding on is that the camera stays where it last looked -- which is exactly
  /// where the object was last seen, and the best guess available.
  /// </remarks>
  private static readonly TimeSpan TargetLost = TimeSpan.FromSeconds(10);

  private readonly string _slaveCameraId;
  private readonly IPtzCommandSender _sender;
  private readonly ILogger _logger;

  private readonly Channel<DetectionSample> _samples = Channel.CreateBounded<DetectionSample>(
    new BoundedChannelOptions(256)
    {
      FullMode = BoundedChannelFullMode.DropOldest,
      SingleReader = true,
      SingleWriter = false,
    });

  private readonly Lock _gate = new();
  private TrackingPolicy _policy;
  private SlaveAim? _aim;
  private TargetKey? _target;
  private bool _explicitTarget;
  private DateTime? _targetSeenAt;
  private string _lastError = string.Empty;

  // Where the camera was last sent, so the dead zone has something to measure against. Null until
  // the first command: the camera's actual position is unknown and, on these cameras, unknowable.
  private PositionAim? _aimedAt;
  private string? _aimedPreset;
  // Moved on whenever ApplyAim reopens the dead zone. A command in flight while that happens was
  // computed by the old binding or calibrations, and must not record where it went once it lands --
  // that would put the reset back.
  private int _aimGeneration;
  private DateTime _lastCommandAt = DateTime.MinValue;

  /// <summary>
  /// While held, a person is driving the camera -- marking out a survey, calibrating it against
  /// landmarks -- and a live session pulling it elsewhere mid-way would make them record a position
  /// they did not choose. So the hold is a correctness requirement of those wizards, not a
  /// convenience. The target keeps being tracked while held: only the commands stop, so when the
  /// hold lapses the camera goes where the object is now, rather than where it was.
  /// </summary>
  private readonly HoldGate _hold;

  /// <param name="hold">The camera's hold, shared with whatever else drives it; a fresh one when omitted.</param>
  public SlaveAimingSession(
    string slaveCameraId,
    IPtzCommandSender sender,
    ILogger logger,
    SlaveAim? aim,
    TrackingPolicy policy,
    HoldGate? hold = null)
  {
    _slaveCameraId = slaveCameraId;
    _sender = sender;
    _logger = logger;
    _aim = aim;
    _policy = policy;
    _hold = hold ?? new HoldGate();
    _logger.LogInformation(
      "Camera '{Camera}': aiming at objects from master '{Master}' by {Method}, {Points} marked point(s)",
      _slaveCameraId,
      aim?.MasterCameraId ?? "(none)",
      aim?.Binding.Method.ToString() ?? "(none)",
      aim?.Binding.Points.Count ?? 0);
  }

  public void Offer(DetectionSample sample) => _samples.Writer.TryWrite(sample);

  /// <summary>
  /// Follows one named object, or goes back to choosing by policy when <paramref name="target"/>
  /// is null.
  /// </summary>
  /// <remarks>
  /// The object is named on the master, because that is the camera whose picture the operator is
  /// looking at and the only one with boxes to click. It is temporary in the same way the
  /// self-guiding override is: once the master has not reported it for <see cref="TargetLost"/>,
  /// the slave goes back to picking for itself rather than staring at where the object used to be.
  /// </remarks>
  public void SetTarget(TargetKey? target)
  {
    lock (_gate)
    {
      _target = target;
      _explicitTarget = target is not null;
      // Not null even for a fresh target: it has not been seen by this session yet, but it must
      // not look lost before its first sighting arrives either.
      _targetSeenAt = target is null ? null : DateTime.UtcNow;
    }
  }

  /// <summary>Takes the binding, and the calibrations that go with it, as they now are -- in one step.</summary>
  /// <remarks>
  /// A resurveyed binding or a recalibrated camera means every position computed before was wrong,
  /// so the dead zone must not suppress the first command after it: on the map method it compares
  /// where the object stands, which a new calibration does not move, and a person standing still
  /// would otherwise be left looked at through the old one. Compared by content, so a model merely
  /// read again (the cache does, once a minute) reopens nothing and cannot cost a needless slew.
  /// </remarks>
  public void ApplyAim(SlaveAim? aim)
  {
    lock (_gate)
    {
      var rebound = Describe(_aim?.Binding) != Describe(aim?.Binding);
      if (rebound || !SlaveAim.SameCalibrations(_aim?.Geo, aim?.Geo))
      {
        _logger.LogInformation(
          rebound ? "Camera '{Camera}': binding is now {Binding}" : "Camera '{Camera}': calibrations changed, {Binding}",
          _slaveCameraId, Describe(aim?.Binding));
        _aimedAt = null;
        _aimedPreset = null;
        _aimGeneration++;
      }
      _aim = aim;
    }
  }

  /// <summary>Records where a command sent the camera, unless the dead zone was reopened meanwhile.</summary>
  private void Landed(int generation, PositionAim? at, string? preset)
  {
    lock (_gate)
    {
      if (generation != _aimGeneration)
        return;
      _aimedAt = at;
      _aimedPreset = preset;
    }
  }

  private int Generation()
  {
    lock (_gate)
      return _aimGeneration;
  }

  public void ApplyPolicy(TrackingPolicy policy)
  {
    lock (_gate)
    {
      if (_target is not null && !policy.Accepts(_target.Source))
      {
        _target = null;
        _explicitTarget = false;
        _targetSeenAt = null;
      }
      _policy = policy;
    }
  }

  public TrackingSnapshot Snapshot()
  {
    lock (_gate)
      return new TrackingSnapshot(
        _slaveCameraId,
        true,
        _target,
        _explicitTarget,
        _targetSeenAt,
        // Reported as the camera's condition rather than as an error, because it is neither a fault
        // nor silence: somebody has the camera, and that is what the monitor should say.
        _hold.IsHeld ? HoldGate.Reason : _lastError,
        _aim?.MasterCameraId);
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
        else
          await ExpireAsync(ct);
      }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Slave aiming loop for camera '{Camera}' stopped", _slaveCameraId);
    }
    // No stop on the way out, unlike the self-guiding loop: every command this session sends is a
    // move to a place, which the camera completes on its own. There is no motor left running.
  }

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
      // A quiet moment in front of the master, not a failure.
    }
    return batch;
  }

  private async Task OnBatchAsync(List<DetectionSample> batch, CancellationToken ct)
  {
    TrackingPolicy policy;
    TargetKey? target;
    bool explicitTarget;
    lock (_gate)
    {
      policy = _policy;
      target = _target;
      explicitTarget = _explicitTarget;
    }

    var usable = TargetChooser.Usable(batch, policy);
    if (usable.Count == 0)
    {
      await ExpireAsync(ct);
      return;
    }

    // The object being followed, if the master still reports it. Nothing here reacquires by
    // proximity or by re-id the way the self-guiding law does: the master does not move, so a
    // track that ends has ended, and there is no scene shifting underneath it to explain away.
    var match = target is null ? null : usable.FirstOrDefault(s => s.Key == target);

    // An object the operator named is not swapped for a neighbour merely because it was missing
    // from one batch — occluded, or just not detected that frame. It is held until it has been
    // gone long enough to count as lost, and only then does the slave start choosing again.
    if (match is null && explicitTarget)
    {
      await ExpireAsync(ct);
      return;
    }

    match ??= TargetChooser.Choose(usable, policy);
    if (match is null)
    {
      await ExpireAsync(ct);
      return;
    }

    lock (_gate)
    {
      _target = match.Key;
      _targetSeenAt = DateTime.UtcNow;
    }

    // The target may be a face — the operator picked one, or the policy prefers them — and a face
    // box has no ground contact to aim by. Trade it for the body it belongs to, which the detector
    // reports under the same track id. See TargetChooser.GroundContact.
    var contact = TargetChooser.GroundContact(match, usable);
    if (contact is null)
    {
      Fail($"Only the {match.Kind.ToString().ToLowerInvariant()} of the target is visible, "
        + "which does not touch the ground and cannot say where to look");
      return;
    }

    await AimAsync(contact, ct);
  }

  /// <summary>Drops a target the master has stopped reporting for long enough.</summary>
  private async Task ExpireAsync(CancellationToken ct)
  {
    SlaveAim? aim;
    lock (_gate)
    {
      if (_target is null || _targetSeenAt is null)
        return;
      if (DateTime.UtcNow - _targetSeenAt.Value < TargetLost)
        return;
      _target = null;
      _explicitTarget = false;
      _targetSeenAt = null;
      aim = _hold.IsHeld ? null : _aim;
    }

    // The camera stays pointing where it is: somewhere the object was recently is a better place to
    // be looking than wherever a home position happens to point. But it zooms out to the widest
    // sample, so that what turns up next nearby is in the picture to be followed, rather than
    // glimpsed through a frame cut for the one that left.
    if (_aimedAt is not { } last || aim?.Geo?.Slave.Zoom is not { } zoom || last.Zoom - zoom.Widest < ZoomThreshold)
      return;
    var generation = Generation();
    _lastCommandAt = DateTime.UtcNow;
    var error = await _sender.AimAsync(_slaveCameraId, last.Pan, last.Tilt, zoom.Widest, ct);
    if (error is not null)
    {
      Fail(error);
      return;
    }
    Landed(generation, last with { Zoom = zoom.Widest }, null);
    _logger.LogDebug("Camera '{Camera}': target lost, zoomed out to {Zoom:F3}", _slaveCameraId, zoom.Widest);
  }

  private async Task AimAsync(DetectionSample target, CancellationToken ct)
  {
    // Taken once: the binding and its calibrations are one thing, and this detection is aimed by
    // one version of them even if the reconcile replaces them meanwhile.
    SlaveAim? aim;
    lock (_gate)
    {
      aim = _aim;
      if (_hold.IsHeld)
        return;
    }

    if (aim is null)
      return;

    var reason = aim.Unusable();
    if (reason is not null)
    {
      Fail(reason);
      return;
    }

    if (DateTime.UtcNow - _lastCommandAt < MinCommandInterval)
      return;

    // The ground contact of the object, which is what the survey mapped. The caller has already
    // made sure this is a box that touches the ground — a face's bottom edge is a chin, and would
    // send the slave to the ground somewhere behind the person.
    var x = target.CenterX;
    var y = target.Top + target.Height;

    var error = aim.ByPreset
      // The presets method gets the whole footprint, not just its middle: which zone an object is
      // in is a question about the object, and it has width.
      ? await AimByPresetAsync(aim.Binding, target.Left, target.Left + target.Width, y, ct)
      : await AimByPositionAsync(aim, x, y, ct);

    if (error is null)
      Succeed();
    else
      Fail(error);
  }

  private async Task<string?> AimByPositionAsync(SlaveAim aim, float x, float y, CancellationToken ct)
  {
    var (position, error) = aim.Position(x, y);
    return position is null ? error : await SendPositionAsync(position, ct);
  }

  /// <summary>Sends the camera to a position, unless it is already about there.</summary>
  private async Task<string?> SendPositionAsync(PositionAim position, CancellationToken ct)
  {
    if (_aimedAt is { } last && AboutThere(last, position))
      return null;

    var generation = Generation();
    _lastCommandAt = DateTime.UtcNow;
    var error = await _sender.AimAsync(_slaveCameraId, position.Pan, position.Tilt, position.Zoom, ct);
    if (error is not null)
      return error;

    Landed(generation, position, null);
    if (_logger.IsEnabled(LogLevel.Debug))
      _logger.LogDebug(
        "Camera '{Camera}': aimed at ({Pan:F3},{Tilt:F3}) for {What}",
        _slaveCameraId, position.Pan, position.Tilt, position.Describe);
    return null;
  }

  /// <summary>Whether the camera, sent to <paramref name="last"/>, already shows <paramref name="next"/>.</summary>
  private static bool AboutThere(PositionAim last, PositionAim next)
  {
    if (last.Ground is { } was && next.Ground is { } now)
    {
      var moved = Math.Sqrt(Math.Pow(now.East - was.East, 2) + Math.Pow(now.North - was.North, 2));
      return moved < Math.Max(GroundMoveThresholdM, next.GroundToleranceM)
        && MathF.Abs(next.Zoom - last.Zoom) < ZoomThreshold;
    }
    return MathF.Abs(next.Pan - last.Pan) < MoveThreshold && MathF.Abs(next.Tilt - last.Tilt) < MoveThreshold;
  }

  private async Task<string?> AimByPresetAsync(
    SlaveBindingModel binding, float left, float right, float y, CancellationToken ct)
  {
    // Weighed by how much of the object lies in each zone rather than by where its centre falls:
    // an object on a boundary is genuinely in two zones, and deciding by one point makes it a coin
    // toss that open-loop aiming obeys every time. See StickyZone.
    var nearest = PtzSlaveBindingMath.StickyZone(
      binding.Points, left, right, y, _aimedPreset, PtzSlaveBindingMath.ZoneHoldShare);
    if (nearest?.PresetToken is null)
      return "No marked point with a preset is near the object";

    // The dead zone is the marked points themselves: an object moving inside one point's patch of
    // the master's frame resolves to the same preset, and the camera is already there.
    if (_aimedPreset == nearest.PresetToken)
      return null;

    var generation = Generation();
    _lastCommandAt = DateTime.UtcNow;
    var error = await _sender.GotoPresetAsync(binding.SlaveCameraId, nearest.PresetToken, ct);
    if (error is not null)
      return error;

    Landed(generation, null, nearest.PresetToken);
    _logger.LogDebug(
      "Camera '{Camera}': sent to preset '{Preset}' for a footprint spanning ({Left:F3}..{Right:F3}, {Y:F3})",
      _slaveCameraId, nearest.PresetName ?? nearest.PresetToken, left, right, y);
    return null;
  }

  private void Succeed()
  {
    lock (_gate)
      _lastError = string.Empty;
  }

  private void Fail(string error)
  {
    lock (_gate)
    {
      // Logged only when it changes: a binding with no presets fails on every detection, and an
      // operator does not need that several times a second to learn it once.
      if (_lastError == error)
        return;
      _lastError = error;
    }
    _logger.LogWarning("Camera '{Camera}': cannot aim — {Error}", _slaveCameraId, error);
  }

  private static string Describe(SlaveBindingModel? binding) =>
    binding is null
      ? "none"
      : $"{binding.MasterCameraId}/{binding.Method}/{binding.Points.Count}:"
        + string.Join(
            ';',
            binding.Points.Select(p =>
              $"{p.MasterX:F4},{p.MasterY:F4},{p.Pan},{p.Tilt},{p.PresetToken}"));
}
