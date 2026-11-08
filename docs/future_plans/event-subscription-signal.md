# A subscription edit does not always reach the router

Found 2026-11-06 while accepting M2.6, on the demo bench.

`check_emit_loop` in `tests/demo/check_demo.py` creates an event subscription, closes
`sim-master/peer` once, and waits up to 75 s for the router to report the emitted event stopped by
the causation chain (`event_routing_suppressed` in the entity-state registry). It failed twice in
three runs.

What the logs show for a failing run:

- `VmsCfg` at 20:56:44 — `Event subscription '…' (check-emit-loop) saved as version 1`.
- `VmsSingleton` — the last reload before that was 20:56:38 (`Loaded 1 enabled event
  subscription(s)`), and the next was 20:58:00, by which time the check had already removed the
  subscription. In the 82 s between them nothing reloaded, although the version is read at most
  every 30 s.
- The single `sensor` event that would have been routed was processed at 20:58:17 with one enabled
  subscription — the demo rule only.

In the run that passed (20:48:34) the router loaded two subscriptions the same second the save
happened, so both the pub/sub signal and the version key worked there.

So the write reached VmsCfg, and neither the `event-subscriptions-changed` signal nor the
`event-subscriptions-version` read made the router see it. It is a race, not a missing wire: the
signal does arrive most of the time.

Why this matters beyond a flaky check: an operator who creates a subscription and closes a contact
once may conclude the rule does not work. The 30-second version read is meant to be the backstop.

What to look at next:

- whether VmsCfg writes `event-subscriptions-version` on every save, and whether the write is in the
  same transaction as the row (a save that commits and a version write that does not would look
  exactly like this);
- whether VmsSingleton's 30-second gate is ever skipped — e.g. the timer reset by an unrelated
  setting change, or the state-store read failing silently and being retried only on the next tick;
- the `event-subscriptions-changed` delivery when the router is busy: a lost message must not be the
  only fast path.
