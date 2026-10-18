using System.Security.Cryptography;
using System.Text;

namespace VmsAnalyticsSingleton;

// The set of video_a workers this instance drives (DAPR_ANALYTICS_WORKER_APP_IDS, comma-separated).
// Watch-to-worker assignment uses rendezvous hashing: stable and stateless — adding/removing a
// worker only migrates the watches whose argmax changed, everything else stays put.
public sealed class AnalyticsWorkerRegistry(IReadOnlyDictionary<string, IAnalyticsWorkerClient> workers)
{
  public IReadOnlyDictionary<string, IAnalyticsWorkerClient> Workers { get; } = workers;

  public string SelectWorker(string cameraId)
  {
    string? best = null;
    var bestScore = ulong.MinValue;
    foreach (var appId in Workers.Keys)
    {
      var score = Score(cameraId, appId);
      if (best is null || score > bestScore)
      {
        best = appId;
        bestScore = score;
      }
    }
    return best ?? throw new InvalidOperationException("No analytics workers configured");
  }

  public IAnalyticsWorkerClient ClientFor(string workerAppId) => Workers[workerAppId];

  private static ulong Score(string cameraId, string workerAppId)
  {
    var hash = MD5.HashData(Encoding.UTF8.GetBytes(cameraId + "|" + workerAppId));
    return BitConverter.ToUInt64(hash, 0);
  }
}
