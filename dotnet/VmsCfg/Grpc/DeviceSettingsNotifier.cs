using System.Text.Json.Serialization;
using Dapr.Client;

namespace VmsCfg;

/// <summary>
/// Announces that one camera's part settings moved: a fresh shared version key for readers that
/// replicate, and the pub/sub signal for the fast path. One place, because a row written without
/// this leaves VmsMap on its old arming until its cache expires — the demo install writes rows too,
/// and it is not a reason to spell the topic twice.
/// </summary>
public sealed class DeviceSettingsNotifier(DaprClient dapr, ILogger<DeviceSettingsNotifier> logger)
{
  private const string Topic = "device-settings-changed";
  private const string VersionKey = "device-settings-version";

  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  private static readonly string StateStoreName =
    Environment.GetEnvironmentVariable("DAPR_STATE_STORE_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'DAPR_STATE_STORE_NAME' is not set");

  // Spelled out: DaprClient serializes with web defaults (camelCase), the subscribers read
  // snake_case, and the mismatch does not fail — it silently deserializes to an empty record.
  private sealed record Payload([property: JsonPropertyName("camera_id")] string CameraId);

  public async Task NotifyAsync(string cameraId, CancellationToken ct)
  {
    try
    {
      // The version key is the truth for replicable readers; the message is the fast signal.
      await dapr.SaveStateAsync(
        StateStoreName, VersionKey, Guid.NewGuid().ToString("N"), cancellationToken: ct);
      await dapr.PublishEventAsync(PubSubName, Topic, new Payload(cameraId), ct);
    }
    catch (Exception ex)
    {
      // The write itself succeeded; a lost signal costs a replica its cache TTL, not the change.
      logger.LogWarning(ex,
        "device-settings-changed publish failed for '{CameraId}' — replicas will pick it up by TTL",
        cameraId);
    }
  }
}
