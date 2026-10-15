using System.Collections.Concurrent;
using Vms.Cfg;
using VmsPtzTrackingSingleton.Geo;

namespace VmsPtzTrackingSingleton;

/// <summary>
/// Every running session, of both kinds, and the one detection stream they share.
/// </summary>
/// <remarks>
/// <para>
/// One session per camera, which is the whole reason this service is a singleton: two of them would
/// send a motor opposing commands. The shared stream is reference-counted against the sessions, so
/// a deployment where nothing tracks costs no stream at all.
/// </para>
/// <para>
/// A camera runs one of two laws, never both. With no master named it guides itself, closing a loop
/// on its own picture (<see cref="TrackingSession"/>); with a master named it is aimed at that
/// camera's objects through a hand-marked survey (<see cref="SlaveAimingSession"/>). The choice is
/// the policy's <c>master_camera_id</c> and nothing else, so it changes by reconfiguration and the
/// old session is torn down before the new one starts.
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
  SlaveBindingCache bindingCache,
  CameraSpatialSource spatial,
  SpatialModelCache spatialCache,
  IPtzCommandSender sender,
  DetectionStreamClient stream,
  ILoggerFactory loggerFactory,
  ILogger<TrackingSessionManager> logger) : IAsyncDisposable
{
  private sealed record Running(TrackingSession Session, CancellationTokenSource Cts, Task Loop);

  private sealed record RunningSlave(
    SlaveAimingSession Session, string MasterCameraId, CancellationTokenSource Cts, Task Loop);

  private readonly ConcurrentDictionary<string, Running> _sessions = new(StringComparer.Ordinal);
  private readonly ConcurrentDictionary<string, RunningSlave> _slaves = new(StringComparer.Ordinal);

  // Cameras told to guide themselves that cannot: no VmsOnvif reaches them, their telemetry flag is
  // clear, or -- for a slave -- the survey that would aim them is missing or unusable. Reported
  // alongside the running ones so the monitor can show the setting as on — and so the operator can
  // switch it off again — instead of the camera silently looking idle.
  private volatile IReadOnlyDictionary<string, string> _blocked =
    new Dictionary<string, string>(StringComparer.Ordinal);

  // The master a blocked camera was configured to follow. Kept beside the reason so the monitor can
  // still tell that the pairing exists — a slave whose survey is short of a preset is listed as not
  // running, and its master's card should go on offering the object-picking gesture regardless.
  private volatile IReadOnlyDictionary<string, string> _blockedMasters =
    new Dictionary<string, string>(StringComparer.Ordinal);

  // Serialises start/stop so the shared stream is opened and closed exactly once, and so a
  // reconcile cannot race an operator's request for the same camera.
  private readonly SemaphoreSlim _gate = new(1, 1);
  private CancellationTokenSource? _pumpCts;
  private Task? _pump;

  /// <summary>
  /// Points everything that follows this camera's picture at one object, or hands them back to
  /// their own priorities when <paramref name="target"/> is null.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The camera named is the one whose picture the operator is looking at, which is where the boxes
  /// they can click are. That is not necessarily the camera that moves: naming an object on a
  /// stationary master points every slave bound to it, and the master itself if it also guides
  /// itself. Both happen, and a master that both tracks and feeds slaves is a legitimate
  /// arrangement rather than a case to choose between.
  /// </para>
  /// <para>
  /// This does not start or stop anything. A camera nothing follows has no session to take over,
  /// and saying so is more useful than quietly starting one that nothing would ever switch off
  /// again.
  /// </para>
  /// </remarks>
  /// <returns>Null on success, otherwise why the request was refused.</returns>
  public Task<string?> SetTargetAsync(string cameraId, TargetKey? target, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(cameraId))
      return Task.FromResult<string?>("cameraId must not be empty");

    var told = 0;
    if (_sessions.TryGetValue(cameraId, out var running))
    {
      running.Session.SetTarget(target);
      told++;
    }

    foreach (var slave in _slaves.Values)
    {
      if (slave.MasterCameraId != cameraId)
        continue;
      slave.Session.SetTarget(target);
      told++;
    }

    if (told > 0)
      return Task.FromResult<string?>(null);

    // A slave named directly is the one case worth its own words: the operator is looking at a
    // camera that moves but has no boxes of its own to click, and the answer is not "switch
    // something on" but "click it on the other picture".
    if (_slaves.TryGetValue(cameraId, out var itself))
      return Task.FromResult<string?>(
        $"Camera '{cameraId}' follows objects seen by '{itself.MasterCameraId}': pick the object there");

    return Task.FromResult<string?>(
      $"Camera '{cameraId}' is not following anything: switch self-guidance on in its settings first");
  }

  /// <summary>Sends a slave to where a spot in its master's frame maps to.</summary>
  /// <remarks>
  /// How a survey is checked without waiting for someone to walk through it. It runs here, beside
  /// the aiming sessions, because this service is the one owner of a camera's motor: a test aim
  /// issued from web_vms.Server would be a second thing driving it, and a slave with a live session
  /// would fight the operator for the camera.
  /// <para>
  /// The binding is re-read rather than taken from the cache. The wizard's next action after saving
  /// a point is to test it, and the invalidation event is still on the bus at that moment.
  /// </para>
  /// </remarks>
  public async Task<AimResult> AimAtMasterPointAsync(
    string slaveCameraId, float masterX, float masterY, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(slaveCameraId))
      return AimResult.Failed("slaveCameraId must not be empty");

    var binding = await bindingCache.ReloadAsync(slaveCameraId, ct);
    if (binding is null)
      return AimResult.Failed($"Camera '{slaveCameraId}' has no binding to a master");

    // Past the cache: the wizard tests a point the moment after saving it, or a calibration the
    // moment after recording it, and the invalidation is still on the bus.
    var slaveAim = await ResolveAimAsync(binding, fresh: true, ct);
    if (slaveAim.Unusable() is { } unusable)
      return AimResult.Failed(unusable);

    if (slaveAim.ByPreset)
    {
      var nearest = PtzSlaveBindingMath.Nearest(binding.Points, masterX, masterY);
      if (nearest?.PresetToken is null)
        return AimResult.Failed("No marked point with a preset is near that spot");

      var presetError = await sender.GotoPresetAsync(slaveCameraId, nearest.PresetToken, ct);
      return presetError is null
        ? new AimResult(true, null, null, null, nearest.PresetToken, nearest.PresetName, false)
        : AimResult.Failed(presetError);
    }

    var (position, positionError) = slaveAim.Position(masterX, masterY);
    if (position is null)
      return AimResult.Failed(positionError!);

    var error = await sender.AimAsync(slaveCameraId, position.Pan, position.Tilt, ct);
    return error is null
      ? new AimResult(true, null, position.Pan, position.Tilt, null, null, position.Extrapolated)
      : AimResult.Failed(error);
  }

  /// <summary>
  /// How long a slave leaves the camera where an operator aimed it on the map before following its
  /// master again. Without it the next detection would swing the camera straight back.
  /// </summary>
  private static readonly TimeSpan WorldAimHold = TimeSpan.FromSeconds(30);

  /// <summary>Points a landmark-calibrated camera at a place picked in some space.</summary>
  /// <param name="spaceId">Where the point was picked; must share coordinates with the camera's own space.</param>
  /// <param name="heightAboveFloorM">Above that space's floor; its elevation is added here.</param>
  /// <remarks>
  /// Read fresh rather than cached, for the same reason as <see cref="AimAtMasterPointAsync"/>: the
  /// calibration wizard checks a landmark the moment after recording it.
  /// </remarks>
  public async Task<WorldAimResult> AimAtWorldPointAsync(
    string cameraId, string spaceId, double lat, double lon, double heightAboveFloorM, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(cameraId))
      return WorldAimResult.Failed("cameraId must not be empty");

    var model = await spatial.LoadAsync(cameraId, ct);
    if (model is null)
      return WorldAimResult.Failed($"Camera '{cameraId}' has no reference placement: mark where it really stands first");

    // Before the calibration: a point in an unrelated world is out of reach however well the camera
    // is calibrated, and saying "calibrate it" would send the operator the wrong way.
    var space = await spatial.LoadSpaceAsync(spaceId, ct);
    if (space is null)
      return WorldAimResult.Failed($"Space '{spaceId}' does not exist");
    if (!model.SharesGeometryWith(space))
      return WorldAimResult.Failed(
        $"The camera stands in '{model.Space.Name}', which shares no coordinates with '{space.Name}'");
    if (model.GeoFit is null)
      return WorldAimResult.Failed(model.GeoFitError ?? "The camera is not calibrated against landmarks");
    var heightAboveGroundM = space.ElevationM + heightAboveFloorM;

    var bearing = model.BearingTo(lat, lon, heightAboveGroundM);
    var aim = PtzGeoMath.Project(model.GeoFit, bearing);

    // Held before the command rather than after it: a detection of the master arriving while the
    // camera travels would otherwise send it straight elsewhere. A command that fails takes the hold
    // back, because the camera has not gone anywhere worth staying at.
    var held = _slaves.TryGetValue(cameraId, out var slave) ? slave.Session : null;
    var holdUntil = DateTime.UtcNow.Add(WorldAimHold);
    var previousHold = held?.HoldUntil(holdUntil);
    var aimed = false;
    try
    {
      var error = await sender.AimAsync(cameraId, aim.Pan, aim.Tilt, ct);
      if (error is not null)
        return WorldAimResult.Failed(error);
      aimed = true;
    }
    finally
    {
      if (!aimed && held is not null)
        held.RestoreHold(holdUntil, previousHold!.Value);
    }

    logger.LogInformation(
      "Aimed '{Camera}' at ({Lat}, {Lon}, {Height} m): azimuth {Az:F1}°, elevation {El:F1}°, {Dist:F0} m -> pan {Pan:F3}, tilt {Tilt:F3}{Extra}",
      cameraId, lat, lon, heightAboveGroundM, bearing.AzimuthDeg, bearing.ElevationDeg, bearing.DistanceM,
      aim.Pan, aim.Tilt, aim.Extrapolated ? " (extrapolated)" : "");
    return new WorldAimResult(true, null, aim, bearing);
  }

  /// <summary>What a camera's landmark observations and marks add up to, for the calibration wizards.</summary>
  /// <returns>Null when the camera has no reference placement.</returns>
  public Task<CameraSpatialModel?> GetSpatialModelAsync(string cameraId, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(cameraId)
      ? Task.FromResult<CameraSpatialModel?>(null)
      : spatial.LoadAsync(cameraId, ct);

  /// <summary>The cameras whose detections can be shown in a space, with their pictures fitted to the ground.</summary>
  /// <remarks>Only those whose marks carry a fit: a camera that cannot be projected has nothing to show.</remarks>
  public async Task<IReadOnlyList<CameraSpatialModel>> ListGroundProjectionsAsync(string spaceId, CancellationToken ct) =>
    (await spatial.LoadForSpaceAsync(spaceId, ct)).Where(m => m.GroundFit is not null).ToList();

  /// <summary>Stops a camera aiming itself for a while, so a person can drive it by hand.</summary>
  /// <remarks>
  /// Renewed by whoever is holding it rather than switched off, so a browser that went away cannot
  /// leave a camera parked. Zero seconds releases immediately, which is what the wizard sends when
  /// it closes.
  /// </remarks>
  /// <returns>Null on success, otherwise why the request was refused.</returns>
  public Task<string?> HoldAsync(string cameraId, TimeSpan duration)
  {
    if (string.IsNullOrWhiteSpace(cameraId))
      return Task.FromResult<string?>("cameraId must not be empty");

    var until = duration <= TimeSpan.Zero ? DateTime.MinValue : DateTime.UtcNow.Add(duration);
    if (!_slaves.TryGetValue(cameraId, out var slave))
      // Not an error worth failing the wizard over: a camera with no running session is already
      // doing nothing, which is what the caller wanted.
      return Task.FromResult<string?>(null);

    slave.Session.HoldUntil(until);
    return Task.FromResult<string?>(null);
  }

  /// <summary>Why a binding cannot aim its camera, or null when it can.</summary>
  /// <remarks>
  /// Through the cache: this answers every read of the binding page, and a calibration changed on
  /// the maps page has already invalidated what it touched by the time anyone looks.
  /// </remarks>
  public async Task<string?> BindingUnusableAsync(SlaveBindingModel binding, CancellationToken ct) =>
    (await ResolveAimAsync(binding, fresh: false, ct)).Unusable();

  /// <summary>Everything the binding aims by: for the map method, both cameras' calibrations.</summary>
  /// <param name="fresh">Read past the cache's TTL, for a caller acting on something just changed.</param>
  private async Task<SlaveAim> ResolveAimAsync(SlaveBindingModel binding, bool fresh, CancellationToken ct)
  {
    if (binding.Method != PtzSlaveBindingMethod.PtzSlaveBindingGeo)
      return SlaveAim.BySurvey(binding);

    var masterRead = spatialCache.LookupAsync(binding.MasterCameraId, fresh, ct);
    var slaveRead = spatialCache.LookupAsync(binding.SlaveCameraId, fresh, ct);
    var (master, slave) = (await masterRead, await slaveRead);

    // A read that failed with nothing held to fall back on says nothing about the camera; reporting
    // it as "no reference placement" would send the administrator to fix what is not broken.
    if ((master.Failed && master.Value is null) || (slave.Failed && slave.Value is null))
      return new SlaveAim(binding, null, "The calibrations could not be read from VmsCfg; retrying");

    var reason = GeoAiming.Unusable(binding.MasterCameraId, master.Value, slave.Value);
    return reason is null
      ? new SlaveAim(binding, new GeoAimPair(master.Value!, slave.Value!), null)
      : new SlaveAim(binding, null, reason);
  }

  /// <summary>What the marked points add up to, for the survey wizard.</summary>
  /// <returns>Null when the camera has no binding at all.</returns>
  public async Task<SlaveBindingModel?> GetBindingFitAsync(string slaveCameraId, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(slaveCameraId)
      ? null
      : await bindingCache.ReloadAsync(slaveCameraId, ct);

  public IReadOnlyList<TrackingSnapshot> States()
  {
    var states = _sessions.Values.Select(r => r.Session.Snapshot()).ToList();
    states.AddRange(_slaves.Values.Select(r => r.Session.Snapshot()));
    foreach (var (cameraId, reason) in _blocked)
    {
      if (!_sessions.ContainsKey(cameraId) && !_slaves.ContainsKey(cameraId))
        states.Add(new TrackingSnapshot(
          cameraId, false, null, false, null, reason, _blockedMasters.GetValueOrDefault(cameraId)));
    }
    return states;
  }

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
    var wantedSlaves = new Dictionary<string, (TrackingPolicy Policy, SlaveAim Aim)>(StringComparer.Ordinal);
    var blocked = new Dictionary<string, string>(StringComparer.Ordinal);
    var blockedMasters = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var (cameraId, policy) in policies)
    {
      if (!policy.Enabled)
        continue;

      var route = await router.ResolveAsync(cameraId, ct);
      if (route is null)
      {
        blocked[cameraId] = "No VmsOnvif instance can reach this camera";
        continue;
      }
      if (!route.HasTelemetry)
      {
        // The flag, not the ONVIF capability: firmware claims a PTZ service whether or not a motor
        // was ever fitted, and driving one of those would look like the camera ignoring us.
        blocked[cameraId] = "The camera is not marked as having telemetry";
        continue;
      }

      if (policy.MasterCameraId is null)
      {
        wanted[cameraId] = policy;
        continue;
      }
      blockedMasters[cameraId] = policy.MasterCameraId;

      var binding = await bindingCache.GetAsync(cameraId, ct);
      if (binding is null)
      {
        blocked[cameraId] = $"No binding to master '{policy.MasterCameraId}' has been marked out yet";
        continue;
      }
      if (binding.MasterCameraId != policy.MasterCameraId)
      {
        // Two places name the master and they disagree. Aiming through a survey marked against a
        // different camera's frame would point the slave somewhere plausible and wrong, which is
        // worse than not moving.
        blocked[cameraId] =
          $"The binding was marked against master '{binding.MasterCameraId}', not '{policy.MasterCameraId}'";
        continue;
      }
      // Re-read on every pass, which the cache makes cheap: a camera recalibrated since the last
      // one reaches the running session here, and one whose calibration was taken away stops.
      var aim = await ResolveAimAsync(binding, fresh: false, ct);
      if (aim.Unusable() is { } unusable)
      {
        blocked[cameraId] = unusable;
        continue;
      }

      wantedSlaves[cameraId] = (policy, aim);
    }

    // Logged only when the set changes: a reconcile runs on a timer and on every camera change in
    // the deployment, and a camera that has been unreachable for an hour must not say so hourly.
    foreach (var (cameraId, reason) in blocked)
    {
      if (!_blocked.ContainsKey(cameraId))
        logger.LogWarning(
          "Camera '{Camera}' is configured to guide itself but cannot: {Reason}", cameraId, reason);
    }
    _blocked = blocked;
    _blockedMasters = blockedMasters;

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

      foreach (var cameraId in _slaves.Keys.ToList())
      {
        if (wantedSlaves.ContainsKey(cameraId))
          continue;
        logger.LogInformation(
          "Camera '{Camera}' no longer follows a master, stopping its session", cameraId);
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

      foreach (var (cameraId, want) in wantedSlaves)
      {
        if (_slaves.TryGetValue(cameraId, out var runningSlave))
        {
          if (runningSlave.MasterCameraId == want.Aim.MasterCameraId)
          {
            runningSlave.Session.ApplyAim(want.Aim);
            runningSlave.Session.ApplyPolicy(want.Policy);
            continue;
          }
          // A new master is a new session, not a patched one: Dispatch routes detections by the
          // master the session was started with, and would otherwise keep feeding it the old
          // master's objects to aim through the new master's calibration.
          logger.LogInformation(
            "Camera '{Camera}' now follows '{Master}' instead of '{Old}', restarting its session",
            cameraId, want.Aim.MasterCameraId, runningSlave.MasterCameraId);
          await StopLockedAsync(cameraId);
        }
        StartSlaveLocked(cameraId, want.Aim, want.Policy);
      }
    }
    finally
    {
      _gate.Release();
    }
  }

  /// <summary>Hands one sighting to everything that has a use for it.</summary>
  /// <remarks>
  /// A camera's detections drive its own session, and every slave that names it as master. Both, in
  /// the case of a camera that guides itself and also feeds a slave — a stationary master with
  /// analytics is the usual arrangement, but nothing about it has to be stationary.
  /// </remarks>
  private void Dispatch(string cameraId, DetectionSample sample)
  {
    if (_sessions.TryGetValue(cameraId, out var running))
      running.Session.Offer(sample);

    // Walked rather than kept as a master-to-slaves index: the number of slaves in a deployment is
    // the number of PTZ cameras someone has surveyed by hand, and an index would be one more thing
    // for a reconcile to keep in step with the sessions themselves.
    foreach (var slave in _slaves.Values)
      if (slave.MasterCameraId == cameraId)
        slave.Session.Offer(sample);
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
    WatchLoop(cameraId, loop);
    logger.LogInformation("Tracking started on camera '{Camera}'", cameraId);
    EnsurePump();
  }

  private void StartSlaveLocked(string cameraId, SlaveAim aim, TrackingPolicy policy)
  {
    var cts = new CancellationTokenSource();
    var session = new SlaveAimingSession(
      cameraId,
      sender,
      loggerFactory.CreateLogger<SlaveAimingSession>(),
      aim,
      policy);
    var loop = session.RunAsync(cts.Token);
    _slaves[cameraId] = new RunningSlave(session, aim.MasterCameraId, cts, loop);
    WatchLoop(cameraId, loop);
    logger.LogInformation(
      "Camera '{Camera}' is now aimed at objects from master '{Master}'",
      cameraId, aim.MasterCameraId);
    EnsurePump();
  }

  // An exception ends a loop without cancelling its CTS, so the dictionary would otherwise keep a
  // dead session (UI still Active, motor idle). Sweep it the same way a Stop does.
  private void WatchLoop(string cameraId, Task loop) =>
    _ = loop.ContinueWith(
      _ => OnLoopEnded(cameraId, loop),
      CancellationToken.None,
      TaskContinuationOptions.None,
      TaskScheduler.Default);

  private void EnsurePump()
  {
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
      var cts = _sessions.TryGetValue(cameraId, out var running) && running.Loop == loop
        ? running.Cts
        : _slaves.TryGetValue(cameraId, out var slave) && slave.Loop == loop
          ? slave.Cts
          : null;
      if (cts is null)
        return;
      if (!cts.IsCancellationRequested)
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
    CancellationTokenSource cts;
    Task loop;
    if (_sessions.TryRemove(cameraId, out var running))
      (cts, loop) = (running.Cts, running.Loop);
    else if (_slaves.TryRemove(cameraId, out var slave))
      (cts, loop) = (slave.Cts, slave.Loop);
    else
      return;

    await cts.CancelAsync();
    // The loop stops the camera on its way out; waiting for it is what makes "tracking off" mean
    // the camera has actually been told to stop.
    try { await loop; } catch (OperationCanceledException) { }
    cts.Dispose();
    logger.LogInformation("Tracking stopped on camera '{Camera}'", cameraId);

    if (!_sessions.IsEmpty || !_slaves.IsEmpty || _pumpCts is null)
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
    foreach (var cameraId in _slaves.Keys.ToList())
      await StopLockedAsync(cameraId);
    _gate.Dispose();
  }
}

/// <summary>Where a test aim sent the camera, or why it did not.</summary>
public sealed record AimResult(
  bool Success,
  string? Error,
  float? Pan,
  float? Tilt,
  string? PresetToken,
  string? PresetName,
  bool Extrapolated)
{
  public static AimResult Failed(string error) => new(false, error, null, null, null, null, false);
}

/// <summary>Where an aim at a place on the map sent the camera, or why it did not.</summary>
public sealed record WorldAimResult(bool Success, string? Error, GeoAim? Aim, Bearing? Bearing)
{
  public static WorldAimResult Failed(string error) => new(false, error, null, null);
}
