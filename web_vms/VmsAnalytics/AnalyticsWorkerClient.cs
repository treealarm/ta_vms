using System.Runtime.CompilerServices;
using Analytics;
using Grpc.Core;

namespace VmsAnalytics;

// Talks to the video_a repository's AnalyticsService over Dapr service invocation
// (DaprClient.CreateInvocationInvoker) — same pattern as CfgServiceClient/FsServiceClient.
// video_a knows nothing about vms_rec's camera model; classes are translated here.
public sealed class AnalyticsWorkerClient(AnalyticsService.AnalyticsServiceClient client) : IAnalyticsWorkerClient
{
  public async Task<bool> StartWatchAsync(
    string watchId,
    string rtspUrl,
    string? credUser,
    string? credPass,
    IReadOnlyList<string> classes,
    float minConfidence,
    uint sampleFps,
    CancellationToken ct)
  {
    var request = new WatchRequest
    {
      WatchId = watchId,
      RtspUrl = rtspUrl,
      MinConfidence = minConfidence,
      SampleFps = sampleFps,
    };
    if (!string.IsNullOrEmpty(credUser)) request.CredUser = credUser;
    if (!string.IsNullOrEmpty(credPass)) request.CredPass = credPass;
    request.Classes.AddRange(classes
      .Select(MapClass)
      .Where(k => k != DetectionKind.Unspecified));

    var response = await client.StartWatchAsync(request, cancellationToken: ct);
    return response.Success;
  }

  public async Task StopWatchAsync(string watchId, CancellationToken ct)
  {
    await client.StopWatchAsync(new StopWatchRequest { WatchId = watchId }, cancellationToken: ct);
  }

  public async IAsyncEnumerable<DetectionEvent> StreamDetectionsAsync([EnumeratorCancellation] CancellationToken ct)
  {
    using var call = client.StreamDetections(new StreamDetectionsRequest(), cancellationToken: ct);
    await foreach (var evt in call.ResponseStream.ReadAllAsync(ct))
      yield return evt;
  }

  private static DetectionKind MapClass(string c) => c.Trim().ToLowerInvariant() switch
  {
    "person" => DetectionKind.Person,
    "face" => DetectionKind.Face,
    "vehicle" => DetectionKind.Vehicle,
    "license_plate" => DetectionKind.LicensePlate,
    _ => DetectionKind.Unspecified,
  };
}
