using Vms.Cfg;

namespace VmsAnalytics;

// Holds the latest ListAnalyticsWatches() snapshot from VmsCfg. Refreshed wholesale (not
// patched) on any signal — analytics-watch-changed / camera-state-changed are only signals to
// pull fresh data, never carriers of state (see CLAUDE.md pub/sub rule).
public sealed class AnalyticsWatchCache(CfgService.CfgServiceClient cfg, ILogger<AnalyticsWatchCache> logger)
{
  private IReadOnlyDictionary<string, AnalyticsWatch> _watches = new Dictionary<string, AnalyticsWatch>();

  public IReadOnlyDictionary<string, AnalyticsWatch> Watches => _watches;

  /// <returns>false when VmsCfg was unreachable and the cache kept its previous contents.</returns>
  public async Task<bool> RefreshAsync(CancellationToken ct = default)
  {
    try
    {
      var resp = await cfg.ListAnalyticsWatchesAsync(new ListAnalyticsWatchesRequest(), cancellationToken: ct);
      _watches = resp.Watches.ToDictionary(w => w.CameraId);
      return true;
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "AnalyticsWatchCache refresh failed");
      return false;
    }
  }
}
