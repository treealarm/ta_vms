using System;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Messaging.PublishSubscribe;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VmsLog;
using VmsMap.Clients;

namespace VmsMap.Adapter
{
  // VmsCfg's event-subscriptions-changed, which today only says the alarm definitions moved and in
  // M2.4d will also say the subscription list did. VmsMap is replicable while the event reaches one
  // replica, so this is the fast signal only: AlarmDefinitionsCache compares the shared
  // subscriptions-version key and reloads on its own clock.
  internal sealed class EventSubscriptionsSubscriber : IHostedService, IDisposable
  {
    private const string Topic = "event-subscriptions-changed";

    private static readonly string PubSubName =
      Environment.GetEnvironmentVariable("PUBSUB_NAME")
        ?? throw new InvalidOperationException("Required environment variable 'PUBSUB_NAME' is not set");

    private readonly DaprPublishSubscribeClient _pubSub;
    private readonly IAlarmDefinitionsSource _definitions;
    private readonly ILogger<EventSubscriptionsSubscriber> _logger;
    private readonly CancellationTokenSource _cts = new();

    public EventSubscriptionsSubscriber(
      DaprPublishSubscribeClient pubSub,
      IAlarmDefinitionsSource definitions,
      ILogger<EventSubscriptionsSubscriber> logger)
    {
      _pubSub = pubSub ?? throw new ArgumentNullException(nameof(pubSub));
      _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
      var options = new DaprSubscriptionOptions(
        new MessageHandlingPolicy(TimeSpan.FromSeconds(10), TopicResponseAction.Retry));

      _ = StreamingSubscriptions.KeepAsync(_pubSub, PubSubName, options,
        [new(Topic, HandleAsync)], _logger, _cts.Token);

      return Task.CompletedTask;
    }

    // The payload is not read: it would only point at what to reload, and the version key already
    // says whether anything moved.
    private Task<TopicResponseAction> HandleAsync(TopicMessage message, CancellationToken ct)
    {
      _definitions.Invalidate();
      return Task.FromResult(TopicResponseAction.Success);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
      _cts.Cancel();
      return Task.CompletedTask;
    }

    public void Dispose() => _cts.Dispose();
  }
}
