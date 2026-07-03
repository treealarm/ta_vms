using Analytics;

namespace VmsAnalytics;

public interface IAnalyticsWorkerClient
{
  Task<bool> StartWatchAsync(
    string watchId,
    string rtspUrl,
    string? credUser,
    string? credPass,
    IReadOnlyList<string> classes,
    float minConfidence,
    uint sampleFps,
    bool attachDebugCrops,
    CancellationToken ct);

  Task StopWatchAsync(string watchId, CancellationToken ct);

  IAsyncEnumerable<DetectionEvent> StreamDetectionsAsync(CancellationToken ct);
}
