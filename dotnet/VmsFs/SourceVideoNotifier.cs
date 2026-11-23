using System;
using System.Threading.Tasks;
using Dapr.Client;

namespace VmsFs;

/// <summary>
/// Announces that a camera's video state moved to <c>Alive</c> or <c>NoVideo</c>.
/// </summary>
/// <remarks>
/// Addressing only, like <c>retention-event-created</c>: the payload carries the camera id and
/// nothing else, and the subscriber reads the state back from this service. One message per camera
/// per report batch, not per stream — a camera with a main and a sub stream changing together is
/// one question for the consumer.
/// </remarks>
public interface ISourceVideoNotifier
{
  void Notify(string cameraId);
}

public sealed class DaprSourceVideoNotifier(
  DaprClient dapr, ILogger<DaprSourceVideoNotifier> logger) : ISourceVideoNotifier
{
  public const string Topic = "camera-video-changed";

  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  public void Notify(string cameraId)
  {
    if (string.IsNullOrWhiteSpace(cameraId))
      return;

    _ = Task.Run(async () =>
    {
      try
      {
        // Snake_case names literally: every subscriber matches them as written, and a camelCase
        // payload deserializes into an all-null record and is dropped in silence.
        await dapr.PublishEventAsync(PubSubName, Topic, new { camera_id = cameraId });
      }
      catch (Exception ex)
      {
        // Not fatal: the periodic sweep converges without the message.
        logger.LogWarning(ex, "Could not publish {Topic} for camera '{CameraId}'", Topic, cameraId);
      }
    });
  }
}
