using System.Text.Json.Serialization;
using Dapr.Client;

namespace VmsMap.PubSub
{
  // Publishes map-config-changed on the deployment's pub/sub component.
  //
  // The field names are spelled out rather than left to a naming policy: DaprClient serializes with
  // web defaults (camelCase), while the subscribers expect snake_case. The mismatch does not fail —
  // it deserializes into an all-null record and the event is dropped in silence.
  internal sealed class DaprMapConfigNotifier : IMapConfigNotifier
  {
    public const string Topic = "map-config-changed";

    private readonly DaprClient _dapr;
    private readonly ILogger<DaprMapConfigNotifier> _logger;
    private readonly string _pubSubName;

    public DaprMapConfigNotifier(
      DaprClient dapr, ILogger<DaprMapConfigNotifier> logger, string pubSubName)
    {
      _dapr = dapr ?? throw new ArgumentNullException(nameof(dapr));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
      _pubSubName = pubSubName ?? throw new ArgumentNullException(nameof(pubSubName));
    }

    public async Task PublishAsync(
      IReadOnlyCollection<string> objectIds,
      IReadOnlyCollection<string> cameraIds,
      CancellationToken ct)
    {
      try
      {
        await _dapr.PublishEventAsync(
          _pubSubName, Topic, new MapConfigChanged(objectIds, cameraIds), ct);
      }
      catch (Exception ex)
      {
        // The write is already committed; a lost notification costs freshness, not correctness.
        _logger.LogWarning(ex, "map-config-changed publish failed");
      }
    }

    private sealed record MapConfigChanged(
      [property: JsonPropertyName("object_ids")] IReadOnlyCollection<string> ObjectIds,
      [property: JsonPropertyName("camera_ids")] IReadOnlyCollection<string> CameraIds);
  }
}
