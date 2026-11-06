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
  /// <summary>One alarm definition as VmsCfg stores it.</summary>
  public sealed record AlarmDefinitionInfo(
    string Id,
    string Name,
    string Severity,
    string AckPolicy);

  /// <summary>Where an alarm's severity and acknowledgement policy are read from.</summary>
  public interface IAlarmDefinitionsSource
  {
    Task<IReadOnlyList<AlarmDefinitionInfo>> ListAsync(CancellationToken ct);
    Task<AlarmDefinitionInfo?> FindAsync(string name, CancellationToken ct);
    void Invalidate();
  }

  /// <summary>
  /// VmsCfg's <c>alarm_definitions</c>, cached as one list. VmsMap is replicable and
  /// <c>event-subscriptions-changed</c> reaches exactly one replica, so the event is only a fast
  /// signal: the truth is the shared <c>subscriptions-version</c> key, read at most once every 30
  /// seconds, with the TTL as the backstop when the state store is unavailable. The same cache will
  /// carry the subscriptions themselves when M2.4d adds them — that is why the key is named after
  /// them and not after the definitions.
  /// </summary>
  public sealed class AlarmDefinitionsCache : IAlarmDefinitionsSource
  {
    private const string VersionKey = "subscriptions-version";
    private static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan VersionCheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private readonly CfgService.CfgServiceClient _cfg;
    private readonly DaprClient _dapr;
    private readonly string _stateStoreName;
    private readonly ILogger<AlarmDefinitionsCache> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<AlarmDefinitionInfo>? _entries;
    private DateTime _loadedAt = DateTime.MinValue;
    private string? _lastVersion;
    private DateTime _lastVersionCheck = DateTime.MinValue;

    public AlarmDefinitionsCache(
      CfgService.CfgServiceClient cfg,
      DaprClient dapr,
      string stateStoreName,
      ILogger<AlarmDefinitionsCache> logger)
    {
      _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
      _dapr = dapr ?? throw new ArgumentNullException(nameof(dapr));
      _stateStoreName = stateStoreName ?? throw new ArgumentNullException(nameof(stateStoreName));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<AlarmDefinitionInfo>> ListAsync(CancellationToken ct)
    {
      await EnsureFreshAsync(ct);

      if (_entries is not null && DateTime.UtcNow - _loadedAt < EntryTtl)
        return _entries;

      await _gate.WaitAsync(ct);
      try
      {
        if (_entries is not null && DateTime.UtcNow - _loadedAt < EntryTtl)
          return _entries;

        try
        {
          var options = new CallOptions(
            deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: ct);
          var response = await _cfg.ListAlarmDefinitionsAsync(
            new ListAlarmDefinitionsRequest(), options);

          _entries = response.Definitions
            .Select(d => new AlarmDefinitionInfo(d.Id, d.Name, d.Severity, d.AckPolicy))
            .ToList();
          _loadedAt = DateTime.UtcNow;
          return _entries;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
          throw;
        }
        catch (Exception ex)
        {
          // Unknown definitions still raise an alarm, with severity 'warning' and the required
          // acknowledgement policy: a VmsCfg hiccup must not turn a sensor into silence.
          _logger.LogWarning(ex,
            "Reading the alarm definitions failed; using the cached copy if any");
          return _entries ?? [];
        }
      }
      finally
      {
        _gate.Release();
      }
    }

    public async Task<AlarmDefinitionInfo?> FindAsync(string name, CancellationToken ct)
    {
      var entries = await ListAsync(ct);
      return entries.FirstOrDefault(d =>
        string.Equals(d.Name, name, StringComparison.Ordinal));
    }

    public void Invalidate()
    {
      _entries = null;
      _loadedAt = DateTime.MinValue;
    }

    private async Task EnsureFreshAsync(CancellationToken ct)
    {
      if (DateTime.UtcNow - _lastVersionCheck < VersionCheckInterval)
        return;

      await _gate.WaitAsync(ct);
      try
      {
        if (DateTime.UtcNow - _lastVersionCheck < VersionCheckInterval)
          return;

        try
        {
          var version = await _dapr.GetStateAsync<string>(
            _stateStoreName, VersionKey, cancellationToken: ct);

          if (_lastVersion is not null && !string.Equals(version, _lastVersion, StringComparison.Ordinal))
            Invalidate();

          _lastVersion = version;
        }
        catch (Exception ex)
        {
          _logger.LogWarning(ex,
            "Reading the subscriptions version failed — definitions expire by TTL");
        }

        _lastVersionCheck = DateTime.UtcNow;
      }
      finally
      {
        _gate.Release();
      }
    }
  }
}
