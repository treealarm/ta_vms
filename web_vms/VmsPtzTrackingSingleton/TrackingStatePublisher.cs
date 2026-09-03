namespace VmsPtzTrackingSingleton;

/// <summary>
/// Puts every running session on the debug-entity-state feed, so the "Entity State" tab can show
/// what each camera is following.
/// </summary>
/// <remarks>
/// On a timer rather than on state changes: the registry in VmsSingleton marks an entry stale after
/// 30 s and sweeps it after five minutes, so a session that is quietly holding a target has to keep
/// saying so. <see cref="VmsLog.EntityStatePublisher"/> is a no-op unless
/// VMS_DEBUG_ENTITY_STATE_SECONDS names a positive period, so this costs nothing when the feed is
/// off, and nothing at all while no camera is being tracked.
/// </remarks>
public sealed class TrackingStatePublisher(
  TrackingSessionManager manager,
  VmsLog.EntityStatePublisher publisher) : BackgroundService
{
  private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    using var timer = new PeriodicTimer(Interval);
    while (await timer.WaitForNextTickAsync(stoppingToken))
    {
      foreach (var state in manager.States())
        publisher.Publish("ptz_tracking", state.CameraId, new
        {
          target_source = state.Target?.Source,
          target_track_id = state.Target?.TrackId,
          target_kind = state.Target?.Kind.ToString(),
          explicit_target = state.ExplicitTarget,
          target_seen_at = state.TargetSeenAt,
          last_error = state.LastError,
        });
    }
  }
}
