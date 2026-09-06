using Grpc.Core;
using Vms.Analytics;

namespace VmsPtzTrackingSingleton;

/// <summary>
/// The live detection firehose, read once for the whole deployment.
/// </summary>
/// <remarks>
/// <para>
/// One <c>StreamCameraDetections</c> call with an empty camera id — the same stream the monitor's
/// bbox overlay is drawn from — demultiplexed to whichever sessions are running. Per-camera calls
/// would cost one stream per tracked camera for exactly the same bytes.
/// </para>
/// <para>
/// A stream rather than the message bus, and this is a correctness point rather than a performance
/// one: the live feed is deliberately lossy end to end (see LiveDetectionBroadcaster's drop-oldest
/// channel), whereas a queue would hold sightings for a consumer that fell behind and later drive
/// the camera towards where the object used to be.
/// </para>
/// </remarks>
public sealed class DetectionStreamClient(
  VmsAnalyticsService.VmsAnalyticsServiceClient analytics,
  ILogger<DetectionStreamClient> logger)
{
  private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

  public async Task ConsumeAsync(Action<string, DetectionSample> dispatch, CancellationToken ct)
  {
    while (!ct.IsCancellationRequested)
    {
      try
      {
        using var call = analytics.StreamCameraDetections(
          new StreamCameraDetectionsRequest { CameraId = string.Empty }, cancellationToken: ct);

        logger.LogInformation("Live detection stream open");

        await foreach (var entry in call.ResponseStream.ReadAllAsync(ct))
        {
          var sample = ToSample(entry);
          if (sample is not null)
            dispatch(entry.CameraId, sample);
        }
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        break;
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Live detection stream dropped, retrying in {Seconds}s", RetryDelay.TotalSeconds);
        try { await Task.Delay(RetryDelay, ct); } catch (OperationCanceledException) { break; }
      }
    }
    logger.LogInformation("Live detection stream closed");
  }

  private static DetectionSample? ToSample(AnalyticsActivityEntry entry)
  {
    // A detection with no geometry says nothing about where to point.
    if (entry.Bbox is null)
      return null;

    return new DetectionSample(
      entry.Source,
      entry.TrackId,
      entry.Kind,
      entry.Bbox.X,
      entry.Bbox.Y,
      entry.Bbox.Width,
      entry.Bbox.Height,
      entry.HasObjectId ? entry.ObjectId : null,
      entry.DetectedAt?.ToDateTime() ?? DateTime.UtcNow);
  }
}
