using System.Runtime.CompilerServices;
using Analytics;

namespace VmsAnalytics;

// Used when DAPR_ANALYTICS_WORKER_APP_ID is not set — analytics ingestion is simply inactive,
// mirroring NoOpSquareIntegration in VmsCfg. No exception, no crash.
public sealed class NoOpAnalyticsWorkerClient : IAnalyticsWorkerClient
{
  public Task<bool> StartWatchAsync(
    string watchId,
    string rtspUrl,
    string? credUser,
    string? credPass,
    IReadOnlyList<string> classes,
    float minConfidence,
    uint sampleFps,
    bool attachDebugCrops,
    CancellationToken ct) => Task.FromResult(false);

  public Task StopWatchAsync(string watchId, CancellationToken ct) => Task.CompletedTask;

  public async IAsyncEnumerable<DetectionEvent> StreamDetectionsAsync([EnumeratorCancellation] CancellationToken ct)
  {
    await Task.CompletedTask;
    yield break;
  }
}
