# Most streaming subscribers never subscribe again

Found 2026-10-23, while fixing the "streaming subscriptions ended" warning of VmsOnvif.

`VmsLog.StreamingSubscriptions` keeps a Dapr streaming subscription open: when the receiver's
`Completion` faults (the sidecar restarted, the connection broke) it disposes of it and subscribes
again with a backoff. `VmsOnvif.CameraConfigSubscriber` and
`VmsAnalyticsSingleton.AnalyticsWatchSubscriber` use it, and both also resync on a timer.

The other subscribers still call `SubscribeAsync` once and throw the receiver away, with no timer
behind them, so a sidecar restart leaves them deaf until the app restarts:

- `VmsFs`: `CameraConfigSubscriber`, `CameraStorageSubscriber`
- `VmsUtils`: `MediaMtxConfigSubscriber`
- `VmsSquareSingleton`: `SquareAlarmSubscriber`, `CameraChangeSubscriber`, `SpaceExportWorker`
- `VmsSingleton`: `ScheduleChangeSignal`, `EntityStateSubscriber`
- `VmsPtzTrackingSingleton`: `CameraConfigSubscriber`

Each is a few lines to move onto the helper. TaVmsHost restarts a sidecar that exits, and compose
does too, so this happens in practice and not only in theory.

Left open by Dapr.Messaging 1.18.7 itself, by reading its receiver: a response stream that ends
without an error ends the fetch loop only, the two loops behind it keep waiting on their channels,
and `Completion` would never complete. Not seen in practice yet — killing a sidecar faulted the
stream, and subscribing while it was down completed `Completion` normally — but nothing rules it
out. 1.18.10 restructures the package (Dapr.Messaging.Runtime, source generators) and was
not taken; whether it reports a clean end is worth checking before the next bump. Until then a
subscriber that must not go stale needs its periodic resync.
