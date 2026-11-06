using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Client;
using Grpc.Core;
using Vms.Cfg;

namespace VmsMap.Clients
{
  /// <summary>
  /// Whether the map should write an <c>analytics</c> row into the journal at all.
  /// </summary>
  /// <remarks>
  /// VmsMap is replicable and <c>event-subscriptions-changed</c> reaches one replica, so a message
  /// is only the fast signal. The truth is VmsCfg's: after every committed upsert or removal it
  /// writes a new random value to the shared <c>subscriptions-version</c> key, and this cache
  /// re-reads the list when that value moves (at most once every 30 s) or when the signal arrives.
  /// <para>
  /// Unlike the alarm-definition cache, a loaded list is kept indefinitely when VmsCfg or the
  /// state store is unavailable: a stale answer is better than silently stopping the gate. With no
  /// list ever loaded the gate fails closed — the journal and the router depend on VmsCfg anyway,
  /// and writing "just in case" is exactly the detection flood the gate exists to prevent.
  /// </para>
  /// <para>
  /// The subscription acts at the moment a detection is processed, with at most 30 seconds of
  /// staleness. That is a notification gate, not a delivery guarantee: enabling a subscription
  /// does not reach detections already handled, and disabling one does not retract rows already
  /// written.
  /// </para>
  /// </remarks>
  public sealed class AnalyticsSubscriptionGate
  {
    private const string VersionKey = "subscriptions-version";
    private static readonly TimeSpan ReloadInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan VersionCheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan WarningInterval = TimeSpan.FromSeconds(30);

    private sealed record Entry(string EventName, bool Enabled);

    private readonly CfgService.CfgServiceClient _cfg;
    private readonly DaprClient _dapr;
    private readonly string _stateStoreName;
    private readonly ILogger<AnalyticsSubscriptionGate> _logger;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private IReadOnlyList<Entry>? _entries;
    private DateTime _loadedAt = DateTime.MinValue;
    private string? _lastVersion;
    private DateTime _lastVersionCheck = DateTime.MinValue;
    private DateTime _lastWarning = DateTime.MinValue;

    public AnalyticsSubscriptionGate(
      CfgService.CfgServiceClient cfg,
      DaprClient dapr,
      string stateStoreName,
      ILogger<AnalyticsSubscriptionGate> logger)
    {
      _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
      _dapr = dapr ?? throw new ArgumentNullException(nameof(dapr));
      _stateStoreName = stateStoreName ?? throw new ArgumentNullException(nameof(stateStoreName));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The fast signal: ask the version next time, but keep what is already known.</summary>
    public void Invalidate() => _loadedAt = DateTime.MinValue;

    public async Task<bool> HasEnabledAsync(string eventName, CancellationToken ct)
    {
      await CheckVersionAsync(ct);

      if (_entries is null || DateTime.UtcNow - _loadedAt >= ReloadInterval)
        await ReloadAsync(ct);

      if (_entries is null)
      {
        WarnThrottled(
          "Event subscriptions are not loaded yet; analytics journaling stays off until VmsCfg answers");
        return false;
      }

      return _entries.Any(e => e.Enabled
        && string.Equals(e.EventName, eventName, StringComparison.Ordinal));
    }

    private async Task CheckVersionAsync(CancellationToken ct)
    {
      if (DateTime.UtcNow - _lastVersionCheck < VersionCheckInterval)
        return;

      await _lock.WaitAsync(ct);
      try
      {
        if (DateTime.UtcNow - _lastVersionCheck < VersionCheckInterval)
          return;

        try
        {
          var version = await _dapr.GetStateAsync<string>(
            _stateStoreName, VersionKey, cancellationToken: ct);

          if (_lastVersion is not null && !string.Equals(version, _lastVersion, StringComparison.Ordinal))
            _loadedAt = DateTime.MinValue;

          _lastVersion = version;
        }
        catch (Exception ex)
        {
          // The last loaded list keeps serving; a lost change waits for the reload interval.
          WarnThrottled($"Reading the subscriptions version failed: {ex.Message}");
        }

        _lastVersionCheck = DateTime.UtcNow;
      }
      finally
      {
        _lock.Release();
      }
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
      await _lock.WaitAsync(ct);
      try
      {
        if (_entries is not null && DateTime.UtcNow - _loadedAt < ReloadInterval)
          return;

        try
        {
          var options = new CallOptions(
            deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: ct);
          var response = await _cfg.ListEventSubscriptionsAsync(
            new ListEventSubscriptionsRequest(), options);

          _entries = response.Subscriptions
            .Select(s => new Entry(s.EventName, s.Enabled))
            .ToList();
          _loadedAt = DateTime.UtcNow;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
          throw;
        }
        catch (Exception ex)
        {
          // Keep the last known answer, if there is one. Without one the gate stays closed.
          _loadedAt = DateTime.UtcNow;
          WarnThrottled($"Reading the event subscriptions failed: {ex.Message}");
        }
      }
      finally
      {
        _lock.Release();
      }
    }

    private void WarnThrottled(string message)
    {
      if (DateTime.UtcNow - _lastWarning < WarningInterval)
        return;

      _lastWarning = DateTime.UtcNow;
      _logger.LogWarning("{Message}", message);
    }
  }
}
