using Analytics;

namespace VmsAnalyticsSingleton;

public interface IAnalyticsWorkerClient
{
  /// <summary>
  /// Starts a watch. Returns the address the worker opened for a publish when
  /// <paramref name="listenForPush"/> was asked for, an empty string when it was not, and null
  /// when the worker refused.
  /// </summary>
  Task<string?> StartWatchAsync(
    string watchId,
    string rtspUrl,
    string? credUser,
    string? credPass,
    IReadOnlyList<string> classes,
    float minConfidence,
    uint sampleFps,
    bool attachDebugCrops,
    bool listenForPush,
    CancellationToken ct);

  Task StopWatchAsync(string watchId, CancellationToken ct);

  IAsyncEnumerable<DetectionEvent> StreamDetectionsAsync(CancellationToken ct);
}
