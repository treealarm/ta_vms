namespace VmsCfg;

/// Watches recording-schedule window boundaries and notifies downstream services when a camera's
/// effective recording state flips.
///
/// The service holds no authoritative state: schedule_active is always recomputed from the wall
/// clock by whoever reads a CameraSetup. This loop only exists so that nobody has to poll — at a
/// boundary it publishes the existing camera-config-changed / media-server-config-changed events,
/// media_server reloads and applies the new policy, and VmsFs drops its caches.
///
/// Sleeps until the exact next boundary rather than on a fixed interval, so recording switches on
/// lead_in_seconds ahead of a window start with sub-second accuracy and off exactly at its end.
public sealed class RecordingScheduleService(
  ScheduleDb scheduleDb,
  ScheduleCache scheduleCache,
  CfgServiceImpl cfgService,
  ILogger<RecordingScheduleService> logger) : BackgroundService
{
  /// Upper bound on a single sleep. A wrong boundary computation then costs at most this much
  /// lateness instead of stalling the schedule until the next edit or restart.
  private static readonly TimeSpan MaxSleep = TimeSpan.FromSeconds(60);
  private static readonly TimeSpan MinSleep = TimeSpan.FromSeconds(1);
  /// Used instead of the next boundary when a notification failed: the boundary that needs
  /// re-announcing is already behind us, and the one ahead may be days away.
  private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

  // Last known active/inactive state per camera. Empty on startup, so the first pass notifies every
  // scheduled camera — idempotent, and it re-syncs anything that changed while we were down.
  private readonly Dictionary<string, bool> _lastActive = [];

  // Set when a boundary could not be announced, so the next wake-up comes soon rather than at the
  // next scheduled boundary.
  private bool _retryPending;

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await TickAsync(stoppingToken);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        return;
      }
      catch (Exception ex)
      {
        logger.LogError(ex, "RecordingScheduleService tick failed");
      }

      await scheduleCache.WaitForChangeAsync(NextSleep(), stoppingToken);
    }
  }

  private async Task TickAsync(CancellationToken ct)
  {
    await scheduleCache.ReloadAsync(ct);

    var now = DateTime.UtcNow;
    var transitions = new List<(string CameraId, bool Active)>();
    var seen = new HashSet<string>();

    // Only cameras with at least one schedule assigned — usually a small set.
    foreach (var (cameraId, serverId) in await scheduleDb.ListCamerasWithSchedulesAsync(ct))
    {
      seen.Add(cameraId);
      var active = scheduleCache.IsCameraActive(cameraId, now);
      if (_lastActive.TryGetValue(cameraId, out var previous) && previous == active)
        continue;

      transitions.Add((cameraId, active));
      logger.LogInformation(
        "Recording schedule window {State} for camera '{CameraId}' on server '{ServerId}'",
        active ? "opened" : "closed", cameraId, serverId);
    }

    // Cameras that lost their last schedule assignment: forget them so a re-assignment is treated
    // as a fresh transition.
    foreach (var cameraId in _lastActive.Keys.Where(id => !seen.Contains(id)).ToList())
      _lastActive.Remove(cameraId);

    if (transitions.Count == 0)
      return;

    // The new state is recorded only once the notification is out. media_server learns about a
    // boundary from that event alone, so treating a failed publish as done would lose the whole
    // window; leaving _lastActive untouched makes the next tick retry.
    if (!await cfgService.NotifyCamerasChangedAsync(transitions.Select(t => t.CameraId).ToList(), ct))
    {
      _retryPending = true;
      logger.LogWarning("Recording schedule notification failed, retrying shortly");
      return;
    }

    _retryPending = false;
    foreach (var (cameraId, active) in transitions)
      _lastActive[cameraId] = active;
  }

  private TimeSpan NextSleep()
  {
    if (_retryPending)
      return RetryDelay;

    var now = DateTime.UtcNow;
    var next = scheduleCache.NextTransitionUtc(now);
    if (next is null)
      return MaxSleep;

    var delay = next.Value - now;
    if (delay < MinSleep) return MinSleep;
    if (delay > MaxSleep) return MaxSleep;
    return delay;
  }
}
