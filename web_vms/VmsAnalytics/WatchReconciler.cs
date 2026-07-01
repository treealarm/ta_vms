using System.Collections.Concurrent;
using Vms.Cfg;

namespace VmsAnalytics;

// Diffs desired watch state (enabled + at least one linked camera armed) against what's
// currently believed to be running on video_a, and issues StartWatch/StopWatch accordingly.
// Not an IHostedService itself — driven by AnalyticsWatchSubscriber on startup and on any
// relevant pub/sub signal.
public sealed class WatchReconciler(IAnalyticsWorkerClient workerClient, ILogger<WatchReconciler> logger)
{
  private readonly ConcurrentDictionary<string, bool> _activeWatchIds = new();

  public async Task ReconcileAsync(IReadOnlyDictionary<string, AnalyticsWatch> watches, CancellationToken ct)
  {
    foreach (var (watchId, watch) in watches)
    {
      var shouldRun = watch.Enabled && watch.Cameras.Any(c => c.Armed);
      var isRunning = _activeWatchIds.ContainsKey(watchId);

      if (shouldRun && !isRunning)
      {
        var started = await workerClient.StartWatchAsync(
          watchId,
          watch.RtspUrl,
          watch.HasCredUser ? watch.CredUser : null,
          watch.HasCredPass ? watch.CredPass : null,
          watch.Classes,
          watch.MinConfidence,
          watch.SampleFps,
          ct);
        if (started)
        {
          _activeWatchIds[watchId] = true;
          logger.LogInformation("Analytics watch '{WatchId}' started", watchId);
        }
      }
      else if (!shouldRun && isRunning)
      {
        await workerClient.StopWatchAsync(watchId, ct);
        _activeWatchIds.TryRemove(watchId, out _);
        logger.LogInformation("Analytics watch '{WatchId}' stopped (disabled or no armed camera)", watchId);
      }
    }

    // Stop watches that disappeared from VmsCfg entirely (removed via RemoveAnalyticsWatch).
    foreach (var watchId in _activeWatchIds.Keys.ToList())
    {
      if (!watches.ContainsKey(watchId))
      {
        await workerClient.StopWatchAsync(watchId, ct);
        _activeWatchIds.TryRemove(watchId, out _);
        logger.LogInformation("Analytics watch '{WatchId}' no longer configured, stopped", watchId);
      }
    }
  }
}
