using System.Collections.Concurrent;

namespace VmsPtzTrackingSingleton;

/// <summary>
/// Every running tracking session, and the one detection stream they share.
/// </summary>
/// <remarks>
/// One session per camera, which is the whole reason this service is a singleton: two of them would
/// send a motor opposing commands. The shared stream is reference-counted against the sessions, so
/// a deployment where nobody is tracking anything costs no stream at all.
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

  // Serialises start/stop so the shared stream is opened and closed exactly once, and so two
  // requests for the same camera cannot both create a session.
  private readonly SemaphoreSlim _gate = new(1, 1);
  private CancellationTokenSource? _pumpCts;
  private Task? _pump;

  /// <returns>Null on success, otherwise why the request was refused.</returns>
  public async Task<string?> SetTrackingAsync(
    string cameraId, bool enabled, TargetKey? target, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(cameraId))
      return "cameraId must not be empty";

    await _gate.WaitAsync(ct);
    try
    {
      if (!enabled)
      {
        await StopLockedAsync(cameraId);
        return null;
      }

      if (_sessions.TryGetValue(cameraId, out var running))
      {
        // Already tracking: this is the operator picking a different object, which must not
        // interrupt the loop or restart the stream.
        running.Session.SetTarget(target);
        return null;
      }

      var route = await router.ResolveAsync(cameraId, ct);
      if (route is null)
        return $"No VmsOnvif instance can reach camera '{cameraId}'";
      if (!route.HasTelemetry)
        // The flag, not the ONVIF capability: firmware claims a PTZ service whether or not a motor
        // was ever fitted, and driving one of those would look like the camera ignoring us.
        return $"Camera '{cameraId}' is not marked as having telemetry";

      StartLocked(cameraId, target, await calibrationCache.GetAsync(cameraId, ct));
      return null;
    }
    finally
    {
      _gate.Release();
    }
  }

  public IReadOnlyList<TrackingSnapshot> States() =>
    _sessions.Values.Select(r => r.Session.Snapshot()).ToList();

  /// <summary>Drops sessions for cameras that may no longer be driven.</summary>
  /// <remarks>
  /// Called when VmsCfg says a camera changed. The event is only a signal to re-pull (CLAUDE.md),
  /// so the current configuration is fetched rather than read out of the payload.
  /// </remarks>
  public async Task ReviewAsync(IReadOnlyCollection<string> cameraIds, CancellationToken ct)
  {
    // An empty list means something node-wide moved, so every session is worth re-checking.
    var candidates = cameraIds.Count == 0 ? _sessions.Keys.ToList() : cameraIds.ToList();

    foreach (var cameraId in candidates)
    {
      if (!_sessions.ContainsKey(cameraId))
        continue;

      var route = await router.ResolveAsync(cameraId, ct);
      if (route is null || !route.HasTelemetry)
      {
        logger.LogInformation(
          "Camera '{Camera}' can no longer be driven, stopping its tracking session", cameraId);
        await _gate.WaitAsync(ct);
        try { await StopLockedAsync(cameraId); }
        finally { _gate.Release(); }
        continue;
      }

      // Calibration is not captured for the life of the session: a wizard/auto run publishes
      // server-config-changed, and the live loop must pick the new matrix up without a restart.
      if (_sessions.TryGetValue(cameraId, out var running))
        running.Session.ApplyCalibration(await calibrationCache.GetAsync(cameraId, ct));
    }
  }

  private void Dispatch(string cameraId, DetectionSample sample)
  {
    if (_sessions.TryGetValue(cameraId, out var running))
      running.Session.Offer(sample);
  }

  private void StartLocked(string cameraId, TargetKey? target, PtzCalibrationModel? calibration)
  {
    var cts = new CancellationTokenSource();
    var session = new TrackingSession(
      cameraId, sender, loggerFactory.CreateLogger<TrackingSession>(), target, calibration);
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
