using Vms.Cfg;
using System.Threading.Channels;

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
  private SlaveBindingModel? _binding;
  private TargetKey? _target;
  private bool _explicitTarget;
  private DateTime? _targetSeenAt;
  private string _lastError = string.Empty;

  // Where the camera was last sent, so the dead zone has something to measure against. Null until
  // the first command: the camera's actual position is unknown and, on these cameras, unknowable.
  private (float Pan, float Tilt)? _aimedAt;
  private string? _aimedPreset;
  private DateTime _lastCommandAt = DateTime.MinValue;
  private DateTime _heldUntil = DateTime.MinValue;

  public SlaveAimingSession(
    string slaveCameraId,
    IPtzCommandSender sender,
    ILogger logger,
    SlaveBindingModel? binding,
    TrackingPolicy policy)
  {
    _slaveCameraId = slaveCameraId;
    _sender = sender;
    _logger = logger;
    _binding = binding;
    _policy = policy;
    _logger.LogInformation(
      "Camera '{Camera}': aiming at objects from master '{Master}' by {Method}, {Points} marked point(s)",
      _slaveCameraId,
      binding?.MasterCameraId ?? "(none)",
      binding?.Method.ToString() ?? "(none)",
      binding?.Points.Count ?? 0);
  }

  public void Offer(DetectionSample sample) => _samples.Writer.TryWrite(sample);

  /// <summary>Stops aiming for a while, so a person can drive the camera by hand.</summary>
  /// <remarks>
  /// Marking out a survey means driving the slave to a spot and recording where it ended up. A live
  /// session pulling the camera somewhere else mid-way makes that impossible, and records a
  /// position the operator did not choose — so the hold is a correctness requirement of the survey
  /// wizard, not a convenience.
  /// <para>
  /// It expires rather than being switched off, and the wizard renews it while it is open. A held
  /// camera is a camera not doing its job, and a browser tab that crashed or a laptop that was
  /// closed must not be able to leave one held forever.
  /// </para>
  /// <para>
  /// The target keeps being tracked while held: only the commands stop. So when the hold lapses the
  /// camera goes where the object is now, rather than replaying where it was when the hold began.
  /// </para>
  /// </remarks>
  /// <returns>The hold this one replaced, for <see cref="RestoreHold"/>.</returns>
  public DateTime HoldUntil(DateTime until)
  {
    lock (_gate)
    {
      var previous = _heldUntil;
      _heldUntil = until;
      return previous;
    }
  }

  /// <summary>Takes back a hold placed by <see cref="HoldUntil"/>, unless someone has held the camera since.</summary>
  public void RestoreHold(DateTime placed, DateTime previous)
  {
    lock (_gate)
    {
      if (_heldUntil == placed)
        _heldUntil = previous;
    }
  }

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

  public void ApplyBinding(SlaveBindingModel? binding)
  {
    lock (_gate)
    {
      // A resurveyed binding means every position computed from the old one was wrong, so the dead
      // zone must not suppress the first command under the new one.
      if (Describe(_binding) != Describe(binding))
      {
        _logger.LogInformation(
          "Camera '{Camera}': binding is now {Binding}", _slaveCameraId, Describe(binding));
        _aimedAt = null;
        _aimedPreset = null;
      }
      _binding = binding;
    }
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
        DateTime.UtcNow < _heldUntil ? "Held while its binding is being marked out" : _lastError,
        _binding?.MasterCameraId);
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
          Expire();
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
      Expire();
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
      Expire();
      return;
    }

    match ??= TargetChooser.Choose(usable, policy);
    if (match is null)
    {
      Expire();
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
  private void Expire()
  {
    lock (_gate)
    {
      if (_target is null || _targetSeenAt is null)
        return;
      if (DateTime.UtcNow - _targetSeenAt.Value < TargetLost)
        return;
      _target = null;
      _explicitTarget = false;
      _targetSeenAt = null;
    }
    // The camera stays where it is. Somewhere the object was recently is a better place to be
    // looking than wherever a home position happens to point.
  }

  private async Task AimAsync(DetectionSample target, CancellationToken ct)
  {
    SlaveBindingModel? binding;
    lock (_gate)
      binding = _binding;

    if (binding is null)
      return;

    lock (_gate)
      if (DateTime.UtcNow < _heldUntil)
        return;

    var reason = binding.Unusable();
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

    var error = binding.Method == PtzSlaveBindingMethod.PtzSlaveBindingPresets
      // The presets method gets the whole footprint, not just its middle: which zone an object is
      // in is a question about the object, and it has width.
      ? await AimByPresetAsync(binding, target.Left, target.Left + target.Width, y, ct)
      : await AimByPositionAsync(binding, x, y, ct);

    if (error is null)
      Succeed();
    else
      Fail(error);
  }

  private async Task<string?> AimByPositionAsync(
    SlaveBindingModel binding, float x, float y, CancellationToken ct)
  {
    var aim = PtzSlaveBindingMath.Project(binding.Fit!, x, y);

    if (_aimedAt is { } last &&
        MathF.Abs(aim.Pan - last.Pan) < MoveThreshold &&
        MathF.Abs(aim.Tilt - last.Tilt) < MoveThreshold)
      return null;

    _lastCommandAt = DateTime.UtcNow;
    var error = await _sender.AimAsync(binding.SlaveCameraId, aim.Pan, aim.Tilt, ct);
    if (error is not null)
      return error;

    _aimedAt = (aim.Pan, aim.Tilt);
    _aimedPreset = null;
    _logger.LogDebug(
      "Camera '{Camera}': aimed at ({Pan:F3},{Tilt:F3}) for master point ({X:F3},{Y:F3}){Extrapolated}",
      _slaveCameraId, aim.Pan, aim.Tilt, x, y,
      aim.Extrapolated ? " — outside the marked area" : string.Empty);
    return null;
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

    _lastCommandAt = DateTime.UtcNow;
    var error = await _sender.GotoPresetAsync(binding.SlaveCameraId, nearest.PresetToken, ct);
    if (error is not null)
      return error;

    _aimedPreset = nearest.PresetToken;
    _aimedAt = null;
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
