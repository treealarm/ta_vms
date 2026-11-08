# ONVIF pull points: what keeps them alive, and why disposal must unsubscribe

Found 2026-11-08 while checking the M2.8 demo: on the bench, after a series of `check_demo.py`
runs and OnvifSim restarts, every camera ended with exactly ten live pull points — OnvifSim's
`PullPoints.MaxPerCamera` — and every new subscription was refused with
`The camera keeps at most 10 pull points`. VmsOnvif then logged `ONVIF event service unavailable,
retrying in 30s` for all five demo cameras, and the device I/O live states went null. Restarting
the simulator and VmsOnvif cleared it.

## What is established

- **The simulator extends a pull point on every `PullMessages`.**
  `EventHandlers.PullMessages` calls `subscription.Extend()` before waiting
  (`../OnvifSim/src/OnvifSim.Server/Onvif/Services/EventHandlers.cs`). A client that keeps polling
  therefore never lets the subscription expire, whatever termination time it originally asked for.
  (`Renew` extends it as well; the sim's sweep only removes what nobody pulls.)
- **Every `EventService1` instance creates a pull point eagerly.** OnvifLib's `InitializeAsync`
  calls `SetupPullPointAsync`, and `ResubscribeAsync` creates a new one on every re-subscribe.
- **The fault path deliberately does not unsubscribe.** `ResubscribeAsync(unsubscribeFirst: false)`
  treats the endpoint as dead and skips `Unsubscribe` to avoid the binding timeout; the comment
  relies on the camera's termination time to reclaim the old pull point. That is only true if
  nothing is still pulling it (see the first point).
- **`EventService1.Dispose` does not wait for the receive loop.** It cancels the token and closes
  the event, pull and subscription-manager clients immediately. The loop's tail — the one place
  that sends `Unsubscribe` (`_subscriptionManagerClient != null && _subscriptionActive`) — can race
  the closed clients and fail silently (it only logs). `StopReceivingAsync` likewise only cancels
  the token.
- **`DeviceIoStateWatcher.DisposeAsync` only stops the receive loop** and does not unsubscribe
  either; it inherits the same race through its `EventService1.Dispose`.
- Each camera has two legitimate consumers, each with a pull point of its own: the ordinary event
  subscription (`OnvifEventSubscriptionService`) and the Device I/O watcher
  (`OnvifDeviceIoWatcherFactory`, a `Camera` of its own because the watcher takes over the event
  service's topic filter). Every re-subscribe, every config restart and every watcher retry is one
  more until the old one is released.

Unsubscribing is the good practice the user asked about: the camera's termination time is a
backstop for a client that vanished, not the way to end a subscription we ended on purpose.

## What to do

1. **Make disposal deterministic.** Stop the receive loop and wait for it (bounded, e.g. a couple of
   seconds), send `Unsubscribe` over the still-open subscription-manager client with a short
   deadline, then close the clients. `EventService1.Dispose` and
   `DeviceIoStateWatcher.DisposeAsync` both need this; a `DisposeAsync` is the natural shape.
2. **Unsubscribe best-effort on the fault path too**, with a short deadline (1–2 s): a dead
   endpoint costs that timeout, but a camera at its cap frees a slot instead of losing the
   subscription for good.
3. **Keep the number of `EventService` instances per camera bounded** — the ordinary subscription,
   one Device I/O watcher, and the one being replaced — and log the camera-advertised
   `MaxPullPoints` when one is refused, so the cause is visible without the simulator's log.
4. **Reproduce and verify on the bench.** Run `check_demo.py` several times with OnvifSim restarts
   in between and assert that no `at most 10 pull points` fault appears and that the simulator
   holds at most two pull points per camera; then a soak with live video and the Device I/O panel
   open. The simulator's count is in its log (`pull point … until` / `… ended`).
5. Decide whether VmsOnvif should share one `Camera` per camera id between the ordinary
   subscription and the Device I/O watcher; they must keep separate topic filters, but the
   connection and the credentials need not be resolved twice.

The fix lives in OnvifLib (a separate repository, released as a NuGet package — see
`onviflib-lives-in-three-places` in the session memory), with VmsOnvif only if ownership changes.
Relevant code: `../OnvifLib/ServicesImp/EventService1.cs`,
`../OnvifLib/DeviceIoStateWatcher.cs`, `dotnet/VmsOnvif/OnvifEventSubscriptionService.cs`,
`dotnet/VmsOnvif/DeviceIoStateService.cs`.
