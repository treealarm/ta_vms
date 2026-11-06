using System.Text.Json;
using Dapr.Messaging.PublishSubscribe;
using Dapr.Messaging.PublishSubscribe.Extensions;
using VmsLog;

namespace VmsSingleton.EventRouting;

/// <summary>
/// The bus end of the router: one message per journal event, carrying only its id.
/// </summary>
/// <remarks>
/// The handler is serialised in-process. VmsSingleton is one instance, but that says nothing about
/// its message dispatcher: two deliveries of the same event — a lost acknowledgement, a sidecar
/// restart — must not both find "no run" and both start one. The run record makes the second a
/// no-op once the first has written it, and the gate makes sure the first has.
/// <para>
/// The message policy's timeout is generous because one subscription may run several actions that
/// each call another service; the retry decision itself is the router's, and the run's attempt
/// counter — not the broker — ends a reaction that keeps failing.
/// </para>
/// </remarks>
internal sealed class EventRouterSubscriber : IHostedService, IDisposable
{
  private static readonly TimeSpan HandlerTimeout = TimeSpan.FromSeconds(60);

  private static readonly JsonSerializerOptions JsonOptions =
    new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

  private static readonly string PubSubName =
    Environment.GetEnvironmentVariable("PUBSUB_NAME")
    ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

  private readonly DaprPublishSubscribeClient _pubSub;
  private readonly EventRouter _router;
  private readonly ILogger<EventRouterSubscriber> _logger;
  private readonly SemaphoreSlim _gate = new(1, 1);
  private readonly CancellationTokenSource _cts = new();

  public EventRouterSubscriber(
    DaprPublishSubscribeClient pubSub,
    EventRouter router,
    ILogger<EventRouterSubscriber> logger)
  {
    _pubSub = pubSub ?? throw new ArgumentNullException(nameof(pubSub));
    _router = router ?? throw new ArgumentNullException(nameof(router));
    _logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  public Task StartAsync(CancellationToken cancellationToken)
  {
    var options = new DaprSubscriptionOptions(
      new MessageHandlingPolicy(HandlerTimeout, TopicResponseAction.Retry));

    _ = StreamingSubscriptions.KeepAsync(_pubSub, PubSubName, options,
      [new(Routing.EventCreatedTopic, HandleAsync)], _logger, _cts.Token);

    return Task.CompletedTask;
  }

  private sealed record EventCreated(string EventId);

  private async Task<TopicResponseAction> HandleAsync(TopicMessage message, CancellationToken ct)
  {
    EventCreated? created;
    try
    {
      created = JsonSerializer.Deserialize<EventCreated>(message.Data.Span, JsonOptions);
    }
    catch (JsonException ex)
    {
      _logger.LogWarning(ex, "Dropping a malformed map-event-created message");
      return TopicResponseAction.Drop;
    }

    if (created is null || !Guid.TryParse(created.EventId, out var eventId))
      return TopicResponseAction.Drop;

    await _gate.WaitAsync(ct);
    try
    {
      return await _router.HandleAsync(eventId, ct) == RouteResult.Retry
        ? TopicResponseAction.Retry
        : TopicResponseAction.Success;
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
      return TopicResponseAction.Retry;
    }
    catch (Exception ex)
    {
      // The router records its own action failures; an exception that reaches here is a failure
      // of the state store or of a configuration read, which a redelivery may well cure.
      _logger.LogWarning(ex, "Routing event {EventId} failed; the message is retried", eventId);
      return TopicResponseAction.Retry;
    }
    finally
    {
      _gate.Release();
    }
  }

  public Task StopAsync(CancellationToken cancellationToken)
  {
    _cts.Cancel();
    return Task.CompletedTask;
  }

  public void Dispose()
  {
    _cts.Dispose();
    _gate.Dispose();
  }
}
