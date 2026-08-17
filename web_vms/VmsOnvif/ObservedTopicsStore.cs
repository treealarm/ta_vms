using System.Collections.Concurrent;
using Dapr.Client;

namespace VmsOnvif;

public sealed record ObservedTopicInfo(string Topic, DateTime FirstSeen, DateTime LastSeen);

public sealed class ObservedTopicsStore(DaprClient dapr, ILogger<ObservedTopicsStore> logger)
{
  // Read from the environment like every other state store consumer, rather than hardcoded:
  // a hardcoded name keeps working only for as long as it happens to match, and CLAUDE.md
  // forbids silent fallbacks for required configuration.
  private static readonly string StateStoreName =
    Environment.GetEnvironmentVariable("DAPR_STATE_STORE_NAME")
    ?? throw new InvalidOperationException(
      "Required environment variable 'DAPR_STATE_STORE_NAME' is not set");

  private const int    TtlDays          = 10;
  private static readonly TimeSpan FlushDebounce = TimeSpan.FromSeconds(5);

  // camera_id -> topic -> info  (in-memory cache)
  private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ObservedTopicInfo>>
    _data = new();

  // camera_id -> last flush time
  private readonly ConcurrentDictionary<string, DateTime> _lastFlush = new();

  public async Task LoadAsync(string cameraId, CancellationToken ct = default)
  {
    try
    {
      var stored = await dapr.GetStateAsync<List<StoredEntry>>(
        StateStoreName, StateKey(cameraId), cancellationToken: ct);
      if (stored == null) return;

      var cameraTopics = _data.GetOrAdd(cameraId, _ => new ConcurrentDictionary<string, ObservedTopicInfo>());
      foreach (var e in stored)
        cameraTopics[e.Topic] = new ObservedTopicInfo(e.Topic, e.FirstSeen, e.LastSeen);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Failed to load observed topics for camera {CameraId}", cameraId);
    }
  }

  public void Record(string cameraId, string topic)
  {
    if (string.IsNullOrEmpty(topic)) return;
    var now = DateTime.UtcNow;
    var cameraTopics = _data.GetOrAdd(cameraId, _ => new ConcurrentDictionary<string, ObservedTopicInfo>());
    cameraTopics.AddOrUpdate(
      topic,
      _ => new ObservedTopicInfo(topic, now, now),
      (_, existing) => existing with { LastSeen = now });
    FlushIfDue(cameraId);
  }

  public IReadOnlyList<ObservedTopicInfo> GetTopics(string cameraId)
  {
    if (!_data.TryGetValue(cameraId, out var topics))
      return [];
    return [.. topics.Values.OrderByDescending(t => t.LastSeen)];
  }

  private void FlushIfDue(string cameraId)
  {
    var now = DateTime.UtcNow;
    var last = _lastFlush.GetOrAdd(cameraId, DateTime.MinValue);
    if (now - last < FlushDebounce) return;
    if (!_lastFlush.TryUpdate(cameraId, now, last)) return;
    _ = Task.Run(() => FlushAsync(cameraId));
  }

  private async Task FlushAsync(string cameraId)
  {
    try
    {
      if (!_data.TryGetValue(cameraId, out var topics)) return;
      var entries = topics.Values
        .Select(t => new StoredEntry(t.Topic, t.FirstSeen, t.LastSeen))
        .ToList();
      var metadata = new Dictionary<string, string>
        { { "ttlInSeconds", (TtlDays * 86400).ToString() } };
      await dapr.SaveStateAsync(StateStoreName, StateKey(cameraId), entries, metadata: metadata);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Failed to flush observed topics for camera {CameraId}", cameraId);
    }
  }

  private static string StateKey(string cameraId) => $"onvif-observed-topics-{cameraId}";

  private sealed record StoredEntry(string Topic, DateTime FirstSeen, DateTime LastSeen);
}
