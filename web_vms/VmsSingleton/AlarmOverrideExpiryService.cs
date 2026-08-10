using Google.Protobuf.WellKnownTypes;
using Vms.Cfg;

namespace VmsSingleton;

/// Clears alarm recording overrides whose window has elapsed and tells the owning media_server to
/// re-read its config.
///
/// An override is stored as state on camera_stream (override_profile_id + alarm_override_until),
/// not as a timer, so nothing expires it on its own: without this sweep the first alarm would pin
/// a camera to continuous recording forever. Activation stays in VmsFs, which is where the
/// retention deadline is computed.
///
/// The sweep lives here rather than in VmsCfg for the same reason as the schedule ticker: VmsCfg is
/// replicated, and periodic global work in a replicated service means every replica doing it. The
/// clearing itself is still VmsCfg's — ExpireAlarmOverrides runs the SQL over its own table and
/// answers with the rows it actually cleared — so no second service touches the schema.
public sealed class AlarmOverrideExpiryService(
  CfgService.CfgServiceClient cfgClient,
  ILogger<AlarmOverrideExpiryService> logger) : BackgroundService
{
  private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

  /// Floor on how often the notification goes out. Each one makes every media server on the affected
  /// node re-read its whole configuration, so a steady stream of alarms would otherwise reload it
  /// every tick. Clearing still happens at Interval, keeping what ListCameras reports accurate; only
  /// the announcement is batched, which at worst leaves a camera recording a few seconds longer than
  /// its window — the safe direction to err in.
  private static readonly TimeSpan MinNotifyInterval = TimeSpan.FromSeconds(5);

  // Cameras already cleared in the database whose notification has not gone out yet. The clear is
  // committed before the publish, and a cleared row no longer looks expired, so re-reading the
  // table cannot recover a failed publish — holding the ids here is what keeps media_server from
  // being left in continuous recording after a transient pub/sub failure.
  private readonly HashSet<string> _pendingNotify = new(StringComparer.Ordinal);
  private DateTime _lastNotifyUtc = DateTime.MinValue;

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
    var expired = await cfgClient.ExpireAlarmOverridesAsync(new Empty(), cancellationToken: ct);
    foreach (var row in expired.Expired)
      _pendingNotify.Add(row.CameraId);

    if (_pendingNotify.Count == 0 || DateTime.UtcNow - _lastNotifyUtc < MinNotifyInterval)
      return;

    var status = await cfgClient.NotifyCamerasChangedAsync(
      new NotifyCamerasChangedRequest { CameraIds = { _pendingNotify } }, cancellationToken: ct);

    if (status.Success)
    {
      _lastNotifyUtc = DateTime.UtcNow;
      _pendingNotify.Clear();
    }
    else
    {
      logger.LogWarning("Alarm override expiry notification failed, retrying shortly");
    }
  }
}
