using System.Collections.Concurrent;

namespace VmsPtzTrackingSingleton;

/// <summary>
/// Every running tracking session, and the one detection stream they share.
/// </summary>
/// <remarks>
/// <para>
/// One session per camera, which is the whole reason this service is a singleton: two of them would
/// send a motor opposing commands. The shared stream is reference-counted against the sessions, so
/// a deployment where nothing tracks costs no stream at all.
/// </para>
/// <para>
/// Which sessions exist is decided by configuration, never by an operator: a session runs for every
/// camera whose self-guidance is switched on and whose telemetry flag says it can actually be
/// driven. <see cref="ReconcileAsync"/> is the only way in, so a restart brings the same set back
/// without anyone pressing anything. What an operator can do is point a running session at a
/// particular object — see <see cref="SetTargetAsync"/>.
/// </para>
/// </remarks>
public sealed class TrackingSessionManager(
  CameraPtzRouter router,
  PtzCalibrationCache calibrationCache,
  IPtzCommandSender sender,
  DetectionStreamClient stream,
  ILoggerFactory loggerFactory,
  ILogger<TrackingSessionManager> logger) : IAsyncDisposable
{
  private sealed record Running(TrackingSession Session, CancellationTokenSource Cts, Task Loop);

  private readonly ConcurrentDictionary<string, Running> _sessions = new(StringComparer.Ordinal);

  // Serialises start/stop so the shared stream is opened and closed exactly once, and so a
  // reconcile cannot race an operator's request for the same camera.
  private readonly SemaphoreSlim _gate = new(1, 1);
  private CancellationTokenSource? _pumpCts;
  private Task? _pump;

  /// <summary>
  /// Points a camera at one object, or hands it back to its own priorities when
  /// <paramref name="target"/> is null.
  /// </summary>
  /// <remarks>
  /// This does not start or stop anything. A camera that is not configured to guide itself has no
  /// session to take over, and saying so is more useful than quietly starting one that nothing
  /// would ever switch off again.
  /// </remarks>
  /// <returns>Null on success, otherwise why the request was refused.</returns>
  public Task<string?> SetTargetAsync(string cameraId, TargetKey? target, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(cameraId))
      return Task.FromResult<string?>("cameraId must not be empty");

    if (!_sessions.TryGetValue(cameraId, out var running))
      return Task.FromResult<string?>(
        $"Camera '{cameraId}' is not following anything: switch self-guidance on in its settings first");

    running.Session.SetTarget(target);
    return Task.FromResult<string?>(null);
  }

  public IReadOnlyList<TrackingSnapshot> States() =>
    _sessions.Values.Select(r => r.Session.Snapshot()).ToList();

  /// <summary>
  /// Brings the running sessions in line with what the configuration asks for.
  /// </summary>
  /// <param name="policies">
  /// Every camera that has a policy row. Cameras absent from it are not configured to track, and a
  /// session for one is stopped.
  /// </param>
  /// <remarks>
  /// Deliberately a full reconcile rather than a diff applied to an event: the event says which
  /// cameras to re-examine, not what changed about them (CLAUDE.md), and an event that arrives late
  /// or out of order must not be able to leave a camera running that should have stopped. Comparing
  /// the whole wanted set against the whole running set cannot drift, whatever order the events
  /// came in.
  /// </remarks>
  public async Task ReconcileAsync(
    IReadOnlyDictionary<string, TrackingPolicy> policies, CancellationToken ct)
  {
    var wanted = new Dictionary<string, TrackingPolicy>(StringComparer.Ordinal);
    foreach (var (cameraId, policy) in policies)
    {
      if (!policy.Enabled)
        continue;

      var route = await router.ResolveAsync(cameraId, ct);
      if (route is null)
      {
        logger.LogWarning(
          "Camera '{Camera}' is configured to guide itself, but no VmsOnvif instance can reach it",
          cameraId);
        continue;
      }
      if (!route.HasTelemetry)
      {
        // The flag, not the ONVIF capability: firmware claims a PTZ service whether or not a motor
        // was ever fitted, and driving one of those would look like the camera ignoring us.
        logger.LogWarning(
          "Camera '{Camera}' is configured to guide itself, but is not marked as having telemetry",
          cameraId);
        continue;
      }

      wanted[cameraId] = policy;
    }

    await _gate.WaitAsync(ct);
    try
    {
      foreach (var cameraId in _sessions.Keys.ToList())
      {
        if (wanted.ContainsKey(cameraId))
          continue;
        logger.LogInformation(
          "Camera '{Camera}' no longer guides itself, stopping its session", cameraId);
        await StopLockedAsync(cameraId);
      }

      foreach (var (cameraId, policy) in wanted)
      {
        // Calibration is not captured for the life of a session: a wizard or auto run publishes
        // server-config-changed, and the live loop must pick the new matrix up without a restart.
        var calibration = await calibrationCache.GetAsync(cameraId, ct);
        if (_sessions.TryGetValue(cameraId, out var running))
        {
          running.Session.ApplyCalibration(calibration);
          running.Session.ApplyPolicy(policy);
          continue;
        }
        StartLocked(cameraId, calibration, policy);
      }
    }
    finally
    {
      _gate.Release();
    }
  }

  private void Dispatch(string cameraId, DetectionSample sample)
  {
    if (_sessions.TryGetValue(cameraId, out var running))
      running.Session.Offer(sample);
  }

  private void StartLocked(string cameraId, PtzCalibrationModel? calibration, TrackingPolicy policy)
  {
    var cts = new CancellationTokenSource();
    var session = new TrackingSession(
      cameraId,
      sender,
      loggerFactory.CreateLogger<TrackingSession>(),
      // No target: a camera coming up chooses for itself. An operator's override is a live thing
      // and is deliberately not restored across a restart.
      target: null,
      calibration,
      policy);
    var loop = session.RunAsync(cts.Token);
    _sessions[cameraId] = new Running(session, cts, loop);
    // An exception ends the loop without cancelling the CTS, so the dictionary would otherwise
    // keep a dead session (UI still Active, motor idle). Sweep it the same way a Stop does.
    _ = loop.ContinueWith(
      _ => OnLoopEnded(cameraId, loop),
      CancellationToken.None,
      TaskContinuationOptions.None,
      TaskScheduler.Default);
    logger.LogInformation("Tracking started on camera '{Camera}'", cameraId);

    if (_pump is not null)
      return;

    _pumpCts = new CancellationTokenSource();
    _pump = stream.ConsumeAsync(Dispatch, _pumpCts.Token);
  }

  private async Task OnLoopEnded(string cameraId, Task loop)
  {
    await _gate.WaitAsync();
    try
    {
      if (!_sessions.TryGetValue(cameraId, out var running) || running.Loop != loop)
        return;
      if (!running.Cts.IsCancellationRequested)
        logger.LogWarning("Tracking loop for camera '{Camera}' ended unexpectedly", cameraId);
      await StopLockedAsync(cameraId);
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "Could not retire the tracking session for camera '{Camera}'", cameraId);
    }
    finally
    {
      _gate.Release();
    }
  }

  private async Task StopLockedAsync(string cameraId)
  {
    if (!_sessions.TryRemove(cameraId, out var running))
      return;

    await running.Cts.CancelAsync();
    // The loop stops the camera on its way out; waiting for it is what makes "tracking off" mean
    // the camera has actually been told to stop.
    try { await running.Loop; } catch (OperationCanceledException) { }
    running.Cts.Dispose();
    logger.LogInformation("Tracking stopped on camera '{Camera}'", cameraId);

    if (!_sessions.IsEmpty || _pumpCts is null)
      return;

    await _pumpCts.CancelAsync();
    try { if (_pump is not null) await _pump; } catch (OperationCanceledException) { }
    _pumpCts.Dispose();
    _pumpCts = null;
    _pump = null;
  }

  public async ValueTask DisposeAsync()
  {
    foreach (var cameraId in _sessions.Keys.ToList())
      await StopLockedAsync(cameraId);
    _gate.Dispose();
  }
}
