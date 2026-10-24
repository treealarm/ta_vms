# A streaming subscription the sidecar closes cleanly goes unnoticed

Found 2026-10-23; what is left of it after every subscriber moved onto
`VmsLog.StreamingSubscriptions` (2026-10-24).

Every streaming subscriber now keeps its subscription through the helper, which opens it again when
the receiver's `Completion` ends — faulted (the sidecar restarted, the connection broke) or not (the
app started before its sidecar: in Dapr.Messaging 1.18.7 that completes without an error). Checked
on the dev stack for VmsFs, VmsUtils and VmsSingleton: app started 20 s before its sidecar, then the
sidecar killed and started again; the queues came back both times and the events reached VmsFs.

Still open, by reading the 1.18.7 receiver: a response stream that ends without an error ends the
fetch loop only, the two loops behind it keep waiting on their channels, and `Completion` would
never complete. Not seen in practice yet, but nothing rules it out, and then the subscriber is deaf
until the app restarts. Only `VmsOnvif.CameraConfigSubscriber` and
`VmsAnalyticsSingleton.AnalyticsWatchSubscriber` have a periodic resync behind them; the others
rely on their caches' TTLs or on nothing.

1.18.10 restructures the package (Dapr.Messaging.Runtime, source generators) and was not taken;
whether it reports a clean end is worth checking before the next bump —
`StreamingSubscriptionsTests.The_real_client_without_a_sidecar_is_retried` is the place to start.
