using Dapr.Client;
using Microsoft.Extensions.Logging;

namespace VmsLog;

// Publishes a debug "entity state" report to the debug-entity-state topic so that
// any entity (camera, stream, subsystem, etc.) can describe its real in-memory
// runtime state for the debug dashboard. Best-effort, fire-and-forget.
public sealed class EntityStatePublisher(DaprClient daprClient, ILogger<EntityStatePublisher> logger)
{
  private const string Topic = "debug-entity-state";

  private static readonly string AppId = Environment.GetEnvironmentVariable("APP_ID")
    ?? throw new InvalidOperationException("Required environment variable 'APP_ID' is not set");

  private static readonly string PubSubName = Environment.GetEnvironmentVariable("PUBSUB_NAME")
    ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  public void Publish(string entityType, string entityId, object state)
  {
    _ = Task.Run(async () =>
    {
      try
      {
        await daprClient.PublishEventAsync(PubSubName, Topic,
          new { service = AppId, entity_type = entityType, entity_id = entityId, state });
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "Entity-state publish failed for {EntityType} '{EntityId}'", entityType, entityId);
      }
    });
  }
}
