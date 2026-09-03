using System.Runtime.CompilerServices;
using Analytics;
using Grpc.Core;

namespace VmsAnalyticsSingleton;

// Talks to the video_a repository's AnalyticsService over Dapr service invocation
// (DaprClient.CreateInvocationInvoker) — same pattern as CfgServiceClient/FsServiceClient.
// video_a knows nothing about ta_vms's camera model; classes are translated here.
public sealed class AnalyticsWorkerClient(AnalyticsService.AnalyticsServiceClient client) : IAnalyticsWorkerClient
{
  public async Task<string?> StartWatchAsync(
    string watchId,
    string rtspUrl,
    string? credUser,
    string? credPass,
    IReadOnlyList<string> classes,
    float minConfidence,
    uint sampleFps,
    bool attachDebugCrops,
    bool listenForPush,
    CancellationToken ct)
  {
    var request = new WatchRequest
    {
      WatchId = watchId,
      RtspUrl = rtspUrl,
      MinConfidence = minConfidence,
      SampleFps = sampleFps,
      AttachDebugCrops = attachDebugCrops,
      ListenForPush = listenForPush,
    };
    if (!string.IsNullOrEmpty(credUser)) request.CredUser = credUser;
    if (!string.IsNullOrEmpty(credPass)) request.CredPass = credPass;
    request.Classes.AddRange(classes
      .Select(MapClass)
      .Where(k => k != DetectionKind.Unspecified));

    var response = await client.StartWatchAsync(request, cancellationToken: ct);
    // In listen mode the worker answers with the address it opened; only it knows what it is
    // reachable at, and the caller has to hand that to whoever produces the stream.
    return response.Success ? response.ListenRtspUrl : null;
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

  public Task<EmbedImageResponse> EmbedImageAsync(
    byte[] jpeg,
    DetectionKind kind,
    BoundingBox? bbox,
    bool detectFirst,
    CancellationToken ct)
  {
    var request = new EmbedImageRequest
    {
      Jpeg = Google.Protobuf.ByteString.CopyFrom(jpeg),
      Kind = kind,
      DetectFirst = detectFirst,
    };
    if (bbox is not null)
      request.Bbox = bbox;
    return client.EmbedImageAsync(request, cancellationToken: ct).ResponseAsync;
  }

  public Task<EstimateGlobalMotionResponse> EstimateGlobalMotionAsync(
    byte[] beforeJpeg,
    byte[] afterJpeg,
    CancellationToken ct)
  {
    var request = new EstimateGlobalMotionRequest
    {
      BeforeJpeg = Google.Protobuf.ByteString.CopyFrom(beforeJpeg),
      AfterJpeg = Google.Protobuf.ByteString.CopyFrom(afterJpeg),
    };
    return client.EstimateGlobalMotionAsync(request, cancellationToken: ct).ResponseAsync;
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
