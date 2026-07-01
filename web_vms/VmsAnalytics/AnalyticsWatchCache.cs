using Vms.Cfg;

namespace VmsAnalytics;

// Holds the latest ListAnalyticsWatches() snapshot from VmsCfg. Refreshed wholesale (not
// patched) on any signal — analytics-watch-changed / camera-state-changed are only signals to
// pull fresh data, never carriers of state (see CLAUDE.md pub/sub rule).
public sealed class AnalyticsWatchCache(CfgService.CfgServiceClient cfg, ILogger<AnalyticsWatchCache> logger)
{
  private IReadOnlyDictionary<string, AnalyticsWatch> _watches = new Dictionary<string, AnalyticsWatch>();

  public IReadOnlyDictionary<string, AnalyticsWatch> Watches => _watches;

  public async Task RefreshAsync(CancellationToken ct = default)
  {
    try
    {
      var resp = await cfg.ListAnalyticsWatchesAsync(new ListAnalyticsWatchesRequest(), cancellationToken: ct);
      _watches = resp.Watches.ToDictionary(w => w.Id);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "AnalyticsWatchCache refresh failed");
    }
  }
}
