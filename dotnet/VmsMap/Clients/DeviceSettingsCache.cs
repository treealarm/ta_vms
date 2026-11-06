using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Client;
using Grpc.Core;
using Vms.Cfg;

namespace VmsMap.Clients
{
  /// <summary>What the operator configured for one camera part.</summary>
  public sealed record DeviceSettingsInfo(
    string CameraId,
    string Kind,
    string Token,
    string Name,
    string ArmMode,
    string? TriggerOn,
    bool AlarmEnabled);

  /// <summary>Where the arming resolver and the node namer read a part's settings from.</summary>
  public interface IDeviceSettingsSource
  {
    Task<IReadOnlyList<DeviceSettingsInfo>> ListAsync(string cameraId, CancellationToken ct);
    Task<DeviceSettingsInfo?> FindAsync(string cameraId, string kind, string token, CancellationToken ct);
    void Invalidate(string cameraId);
  }

  /// <summary>
  /// VmsCfg's <c>device_settings</c>, cached by camera. VmsMap is replicable, and pub/sub delivers
  /// an event to exactly one replica, so <c>device-settings-changed</c> is only a fast signal: the
  /// truth is the shared <c>device-settings-version</c> key, read at most once every 30 seconds,
  /// with the entry TTL as the backstop when the state store is unavailable.
  /// </summary>
  public sealed class DeviceSettingsCache : IDeviceSettingsSource
  {
    private const string VersionKey = "device-settings-version";
    private static readonly TimeSpan EntryTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan VersionCheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private sealed record Entry(DateTime LoadedAt, IReadOnlyList<DeviceSettingsInfo> Settings);

    private readonly CfgService.CfgServiceClient _cfg;
    private readonly DaprClient _dapr;
    private readonly string _stateStoreName;
    private readonly ILogger<DeviceSettingsCache> _logger;

    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _versionGate = new(1, 1);
    private string? _lastVersion;
    private DateTime _lastVersionCheck = DateTime.MinValue;

    public DeviceSettingsCache(
      CfgService.CfgServiceClient cfg,
      DaprClient dapr,
      string stateStoreName,
      ILogger<DeviceSettingsCache> logger)
    {
      _cfg = cfg;
      _dapr = dapr;
      _stateStoreName = stateStoreName;
      _logger = logger;
    }

    public async Task<IReadOnlyList<DeviceSettingsInfo>> ListAsync(
      string cameraId, CancellationToken ct)
    {
      await EnsureFreshAsync(ct);

      if (_cache.TryGetValue(cameraId, out var cached)
          && DateTime.UtcNow - cached.LoadedAt < EntryTtl)
      {
        return cached.Settings;
      }

      try
      {
        var options = new CallOptions(
          deadline: DateTime.UtcNow.Add(CallTimeout), cancellationToken: ct);
        var response = await _cfg.ListDeviceSettingsAsync(
          new ListDeviceSettingsRequest { CameraId = cameraId }, options);

        var settings = response.Settings
          .Select(s => new DeviceSettingsInfo(
            s.CameraId,
            s.Kind,
            s.Token,
            s.Name,
            s.ArmMode,
            string.IsNullOrWhiteSpace(s.TriggerOn) ? null : s.TriggerOn,
            s.AlarmEnabled))
          .ToList();

        _cache[cameraId] = new Entry(DateTime.UtcNow, settings);
        return settings;
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        throw;
      }
      catch (Exception ex)
      {
        // A part's settings are advisory until the next successful read: a VmsCfg hiccup must not
        // take a sensor's arming away.
        _logger.LogWarning(ex,
          "Reading device settings for camera '{CameraId}' failed; using the cached copy", cameraId);
        return cached?.Settings ?? [];
      }
    }

    public async Task<DeviceSettingsInfo?> FindAsync(
      string cameraId, string kind, string token, CancellationToken ct)
    {
      var settings = await ListAsync(cameraId, ct);
      return settings.FirstOrDefault(s =>
        string.Equals(s.Kind, kind, StringComparison.Ordinal)
        && string.Equals(s.Token, token, StringComparison.Ordinal));
    }

    public void Invalidate(string cameraId) => _cache.TryRemove(cameraId, out _);

    private async Task EnsureFreshAsync(CancellationToken ct)
    {
      if (DateTime.UtcNow - _lastVersionCheck < VersionCheckInterval)
        return;

      await _versionGate.WaitAsync(ct);
      try
      {
        if (DateTime.UtcNow - _lastVersionCheck < VersionCheckInterval)
          return;

        try
        {
          var version = await _dapr.GetStateAsync<string>(
            _stateStoreName, VersionKey, cancellationToken: ct);

          // The first observation establishes the baseline; a change since then is what makes the
          // cache stale. A write of the same value is not a change, and GUIDs make that unlikely.
          if (_lastVersion is not null && !string.Equals(version, _lastVersion, StringComparison.Ordinal))
            _cache.Clear();

          _lastVersion = version;
        }
        catch (Exception ex)
        {
          _logger.LogWarning(ex,
            "Reading the device-settings version failed — entries expire by TTL");
        }

        _lastVersionCheck = DateTime.UtcNow;
      }
      finally
      {
        _versionGate.Release();
      }
    }
  }
}
