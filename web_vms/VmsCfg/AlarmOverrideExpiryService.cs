namespace VmsCfg;

/// Clears alarm recording overrides whose window has elapsed and tells the owning media_server to
/// re-read its config.
///
/// An override is stored as state on camera_stream (override_profile_id + alarm_override_until),
/// not as a timer, so nothing expires it on its own: without this sweep the first alarm would pin
/// a camera to continuous recording forever. Activation stays in VmsFs, which is where the
/// retention deadline is computed — only the expiry lives here, next to the recording-schedule
/// ticker, because it is global periodic work over VmsCfg's own table and must run exactly once.
public sealed class AlarmOverrideExpiryService(
  CameraStorage storage,
  CfgServiceImpl cfgService,
  ILogger<AlarmOverrideExpiryService> logger) : BackgroundService
{
  private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

  // Cameras already cleared in the database whose notification has not gone out yet. The clear is
  // committed before the publish, and a cleared row no longer looks expired, so re-reading the
  // table cannot recover a failed publish — holding the ids here is what keeps media_server from
  // being left in continuous recording after a transient pub/sub failure.
  private readonly HashSet<string> _pendingNotify = new(StringComparer.Ordinal);

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await Task.Delay(Interval, stoppingToken);
        await TickAsync(stoppingToken);
      }
      catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
      {
        return;
      }
      catch (Exception ex)
      {
        logger.LogError(ex, "AlarmOverrideExpiryService tick failed");
      }
    }
  }

  private async Task TickAsync(CancellationToken ct)
  {
    foreach (var (cameraId, streamName) in await storage.ListAlarmOverrideStreamsAsync(ct))
    {
      if (!await storage.ClearAlarmOverrideAsync(cameraId, streamName, ct))
        continue;

      logger.LogInformation(
        "Alarm recording expired for camera '{CameraId}' stream='{StreamName}'", cameraId, streamName);
      _pendingNotify.Add(cameraId);
    }

    if (_pendingNotify.Count == 0)
      return;

    if (await cfgService.NotifyCamerasChangedAsync([.. _pendingNotify], ct))
      _pendingNotify.Clear();
    else
      logger.LogWarning("Alarm override expiry notification failed, retrying shortly");
  }
}
