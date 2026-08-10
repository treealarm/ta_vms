using System.Text.Json;
using Dapr.Messaging.PublishSubscribe;

namespace VmsSingleton;

// Subscribes to the debug-entity-state topic: any entity in any service can
// periodically publish its real in-memory runtime state here for debugging.
public sealed class EntityStateSubscriber(
  DaprPublishSubscribeClient pubSubClient,
  EntityStateRegistry registry,
  ILogger<EntityStateSubscriber> logger) : IHostedService, IDisposable
{
  private const string Topic = "debug-entity-state";
  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
      ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  // Envelope wire format uses snake_case (e.g. "entity_type"), unlike the PascalCase record properties.
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
  };

  private readonly CancellationTokenSource _cts = new();

  private sealed record EntityStateEnvelope(string Service, string EntityType, string EntityId, JsonElement State);

  public Task StartAsync(CancellationToken cancellationToken)
  {
    _ = RunAsync(_cts.Token);
    return Task.CompletedTask;
  }

  private async Task RunAsync(CancellationToken ct)
  {
    try
    {
      await pubSubClient.SubscribeAsync(
        PubSubName,
        Topic,
        new DaprSubscriptionOptions(
          new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Drop)),
        HandleAsync,
        ct);
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
      logger.LogError(ex, "EntityStateSubscriber error");
    }
  }

  private Task<TopicResponseAction> HandleAsync(TopicMessage message, CancellationToken _)
  {
    try
    {
      var envelope = JsonSerializer.Deserialize<EntityStateEnvelope>(message.Data.Span, JsonOptions);
      if (envelope is not null
        && !string.IsNullOrWhiteSpace(envelope.Service)
        && !string.IsNullOrWhiteSpace(envelope.EntityType)
        && !string.IsNullOrWhiteSpace(envelope.EntityId))
      {
        registry.Upsert(envelope.Service, envelope.EntityType, envelope.EntityId, envelope.State.GetRawText());
      }
      else
      {
        logger.LogWarning("Dropping malformed debug-entity-state message");
      }
      return Task.FromResult(TopicResponseAction.Success);
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Dropping malformed debug-entity-state message");
      return Task.FromResult(TopicResponseAction.Drop);
    }
  }

  public Task StopAsync(CancellationToken _)
  {
    _cts.Cancel();
    return Task.CompletedTask;
  }

  public void Dispose() => _cts.Dispose();
}
