# "Streaming subscriptions ended" is logged on every start, while they are running

Found 2026-10-23.

`VmsOnvif.CameraConfigSubscriber` (and `VmsAnalyticsSingleton.AnalyticsWatchSubscriber`, which it
mirrors) awaits the tasks of its `SubscribeAsync` calls and, when they complete, warns
"streaming subscriptions ended — relying on the periodic fallback invalidation until restart".

In Dapr.Messaging 1.18.5 `DaprPublishSubscribeClient.SubscribeAsync` returns
`Task<IAsyncDisposable>`: it completes as soon as the subscription is established and hands back
the receiver, which keeps running (`DaprPublishSubscribeGrpcClient`). So the warning fires right
after a successful subscription — on every start, on every deployment — and the queues it claims
are dead have their consumer (RabbitMQ shows one on each).

The comments around it describe the Redis Streams era (a resubscribe loop that leaked consumers);
whether a stream that really drops is noticed at all now is not known. To do: log the
subscription as established, keep the returned receivers and dispose of them on stop, and find out
what `PublishSubscribeReceiver` does when the sidecar ends the stream, before deciding whether
anything should resubscribe.
