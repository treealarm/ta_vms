using System.Threading.Channels;

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
  /// Inside this much of the centre the object is centred well enough; moving further would hunt.
  private const float DeadZone = 0.06f;
  private const float Gain = 1.6f;
  /// Below this a PTZ motor commonly does not break away at all, so a smaller command is no command.
  private const float MinSpeed = 0.10f;
  /// Past this the picture smears while panning and the detector loses the object it is chasing.
  private const float MaxSpeed = 0.70f;

  private static readonly TimeSpan Pulse = TimeSpan.FromMilliseconds(350);
  private static readonly TimeSpan MinCommandInterval = TimeSpan.FromMilliseconds(200);
  /// Backstop for a Stop that failed: the camera's own PT3S timeout is the outer one.
  private static readonly TimeSpan SafetyStop = TimeSpan.FromMilliseconds(2500);
  private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);

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
  private DateTime _stopAt;
  private DateTime _lastCommandAt = DateTime.MinValue;

  public TrackingSession(string cameraId, IPtzCommandSender sender, ILogger logger, TargetKey? target)
  {
    _cameraId = cameraId;
    _sender = sender;
    _logger = logger;
    _target = target;
    _explicitTarget = target is not null;
    // A freshly named target has not been seen by this session yet, but it must not look lost
    // before its first sighting arrives either.
    _targetSeenAt = target is null ? null : DateTime.UtcNow;
  }

  public void Offer(DetectionSample sample) => _samples.Writer.TryWrite(sample);

  public void SetTarget(TargetKey? target)
  {
    lock (_gate)
    {
      _target = target;
      _explicitTarget = target is not null;
      _targetSeenAt = target is null ? null : DateTime.UtcNow;
      _lastObjectId = null;
      if (target is null) { _lastX = 0.5f; _lastY = 0.5f; }
    }
  }

  public TrackingSnapshot Snapshot()
  {
    lock (_gate)
      return new TrackingSnapshot(_cameraId, _target, _explicitTarget, _targetSeenAt, _lastError);
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

    await DriveAsync(match.CenterX, match.CenterY, ct);
  }

  private DetectionSample? Match(List<DetectionSample> batch)
  {
    TargetKey? target;
    DateTime? seenAt;
    float lastX, lastY;
    string? lastObjectId;
    lock (_gate)
    {
      target = _target;
      seenAt = _targetSeenAt;
      lastX = _lastX;
      lastY = _lastY;
      lastObjectId = _lastObjectId;
    }

    // No target: whatever turned up first is it. This is the "operator armed the mode and picked
    // nobody" case, and the first object through the frame is what they meant.
    if (target is null)
      return batch[0];

    foreach (var sample in batch)
      if (sample.Key == target)
        return sample;

    // Present a moment ago — wait for it rather than grabbing a neighbour.
    if (seenAt is not null && DateTime.UtcNow - seenAt.Value < ReacquireAfter)
      return null;

    // Re-identification first: it is the one signal that means "the same physical object" across a
    // change of track id, which is what a moving camera causes.
    if (lastObjectId is not null)
      foreach (var sample in batch)
        if (sample.ObjectId == lastObjectId)
          return sample;

    DetectionSample? nearest = null;
    var nearestDistance = ReacquireRadius;
    foreach (var sample in batch)
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
  private static float Velocity(float error)
  {
    var magnitude = MathF.Abs(error);
    if (magnitude <= DeadZone)
      return 0f;
    var speed = Math.Clamp(MinSpeed + (Gain * (magnitude - DeadZone)), MinSpeed, MaxSpeed);
    return MathF.Sign(error) * speed;
  }

  private async Task DriveAsync(float centerX, float centerY, CancellationToken ct)
  {
    var pan = Velocity(centerX - 0.5f);
    // The overlay's y grows downward and ONVIF's tilt is positive upward.
    var tilt = -Velocity(centerY - 0.5f);

    if (pan == 0f && tilt == 0f)
    {
      await SafeStopAsync(ct);
      return;
    }

    var now = DateTime.UtcNow;
    if (now - _lastCommandAt < MinCommandInterval)
      return;

    RecordError(await _sender.MoveAsync(_cameraId, pan, tilt, ct));
    _lastCommandAt = now;
    _moving = true;
    _stopAt = now + Pulse;
  }

  private async Task TickAsync(CancellationToken ct)
  {
    var now = DateTime.UtcNow;

    if (_moving && (now >= _stopAt || now - _lastCommandAt > SafetyStop))
      await SafeStopAsync(ct);

    TargetKey? target;
    DateTime? seenAt;
    lock (_gate)
    {
      target = _target;
      seenAt = _targetSeenAt;
    }

    if (target is null || seenAt is null || now - seenAt.Value <= Lost)
      return;

    // Gone for good. The session stays armed and drops back to taking whatever appears next —
    // an explicitly chosen target that has left the scene would otherwise leave the mode switched
    // on and doing nothing at all.
    await SafeStopAsync(ct);
    lock (_gate)
    {
      _target = null;
      _explicitTarget = false;
      _targetSeenAt = null;
      _lastObjectId = null;
      _lastX = 0.5f;
      _lastY = 0.5f;
    }
    _logger.LogInformation("Camera '{Camera}': target lost, waiting for the next object", _cameraId);
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
