# States, events and rights on the map

> Part of [Maps & plans](../maps-and-plans.md): the model and where things live are there.

## States, events and rights

The tree is also where the system's states, journal and permissions live; nothing in video_srv,
VmsFs, VmsOnvif or VmsCfg is written for the map's sake.

- **Every camera is a node from creation.** The adapter (`VmsMap/Adapter/`) creates the canonical
  node from `server-config-changed`, **always under its server node**: the canonical tree of a
  device is one place, and where the camera is drawn is a replica's business. Any placement — map,
  plan or diagram — is a replica (`objects.owner_id`) with its own view; the reference placement
  (`is_reference`, what aiming and calibration go by) belongs to a replica, not to the canonical
  node, which carries no `visual` and no `geo_objects`. `device_node` (`source_kind`, `source_id`,
  `part_kind`, `part_token`) maps a device or one of its parts back to its node, because the node id
  is a one-way MD5; parts are rows of the same registry with their own `part_kind`. A camera VmsCfg
  no longer lists is removed with its replicas, placements and observations.
- **Device rights are anchored to the space of the reference replica.** A canonical node under the
  server does not inherit a right granted on the space the camera is drawn in, so a device node (a
  camera or one of its parts) is also checked from its reference replica and that replica's space;
  with no reference replica the server node's path is all there is.
- **A device's parts are nodes of the same device, and a node need not be placed.** A digital input
  or a relay is a canonical node under its camera (`device_node.part_kind`), created from
  `device-event`/`device-snapshot` and named by the settings that configure it; it has no geometry
  and no placement of its own, and is drawn only when placed as an ordinary replica (see
  [Maps & plans](../maps-and-plans.md), Model). **Admin → Nodes** shows the whole
  logical tree (server → camera → parts) a level at a time, with a server-side search; **Admin →
  Maps → Devices** is the placement palette (a click makes a picture, and the first placement
  becomes the reference); the map's **Nodes** tab is only what is drawn on that canvas.
- **States come from their owners, not from the map.** `retention-event-created` becomes `ALARM`
  plus one `alarm` event (the event id is the episode's `event_uuid`, so a redelivery is a no-op);
  a sweep re-checks VmsFs before it puts `NORM` back. `CONNECTED`/`DISCONNECTED` come from VmsFs's
  open source outages — not from a MediaMTX path, which `video_srv` tears down when nobody watches.
  `camera-state-changed` re-derives a camera's arming and re-checks its sensors — it does not write
  a property on the camera's node: the camera's mode is the map's row and its flag is VmsCfg's,
  which the map writes. The alarm counters bubble up `alarm_states` in the same transaction as the
  state write.
- **An alarm reaches the room the camera stands in.** The canonical tree ends at the server, so the
  counters alone would stop above the camera. A camera whose row changes has that row mirrored onto
  its **reference replica** — the picture on the floor plan — and the spaces above it recount; a
  picture's row is never counted as a second alarm, and only the reference replica gets one. When a
  placement moves the reference, the old picture's row goes and the new one's appears.
- **Device events are monotonic.** The publisher's `(epoch, seq)` decides what is accepted; a clock
  is only the fallback across processes. A repeat of the value already stored is not a transition
  and writes no journal row, but it still advances the cursor — that is how a lost message or a
  restart is repaired.
- **`ALARM` is the condition; acknowledgement is the operator's mark.** ack is refused (409) while
  the condition holds on an armed node; it never removes `ALARM`, and closing is what does.
- **The routing engine is VmsSingleton's module**, not VmsMap's: VmsMap writes the journal row and
  its outbox row in one transaction, and the router trusts `map-event-created` for the id only — it
  re-reads the event, the subscriptions and the arming. A run's progress, cooldowns and diagnostics
  live in the shared state store, so a second instance would degrade delivery but not duplicate it.
- **An analytics detection reaches the journal only while an enabled `analytics` subscription asks
  for one.** The detection stays in `ta_analytics` regardless; `map_event_at` and the publisher's
  sweep make a crash between the insert and the bus harmless. VmsMap's gate is evaluated when the
  detection is processed (≤30 s of staleness) and fails closed until it has loaded the
  subscriptions once.
- **Rights are rows on tree nodes**: `rights(object_id, subject, action)`, with `subject` a Keycloak
  role or user name and `action` from the open set `RightActions`. They inherit downwards; `admin`
  bypasses them and the single open/enforced switch decides what a node with no rows means. VmsMap
  computes the effective set (`GetEffectiveRights`) and also applies it to every multi-node read, so
  a node the caller may not see never leaves the service. web_srv's `RightsService` caches that set
  per user and drops it on `rights-version`; `[RequiresRight(...)]` replaces the old camera ACL
  attributes. VmsCfg keeps no permission tables.
- **`Watch`** is one server stream per client (`box`, `ids`, `values` in the request; a change of
  view is a new stream, because Dapr proxies server-streaming but not a bidirectional call). One
  poller per VmsMap process reads every watched set once a second and diffs by xmin; web_srv serves
  it as `api/map/live` SSE, and the browser fetches the changed nodes over `api/map/*`. A box scope
  also states its completeness: `set_box_truncated` (`{"truncated": true|false}`, no items) is sent
  on the first poll and whenever the answer changes, because the box read is capped at 10 000
  figures; an id scope lists its nodes explicitly and never sends it.

## Alarms on the map

An alarm here is what the rest of ta_vms already calls one: a retention episode in VmsFs, opened
and renewed by `RetainEvent` (VmsOnvif for a matched ONVIF event, VmsAnalyticsSingleton for an
analytics detection). Its `alarm_end_ts` is always set — the projected end every renewal pushes
forward — so an alarm is **running** while that is in the future, and there is no such thing as one
left open by a crash.

- A running alarm is a state on the camera's node, and the operator's feed reads it from VmsMap —
  `POST api/map/states/alarmed` with an empty list, which asks for every alarmed node the caller may
  see — every 15 s. A slow poll, not the live stream: an alarm is a state, and a state has to be
  right across replicas.
- Alarms that have already ended come from the journal instead: `api/map/events` over the last
  15 minutes (`RECENT_ALARM_MINUTES`), filtered to `alarm` events, polled every 60 s. The start of
  such an event may be older than the window — an alarm that ran for an hour and ended a minute ago
  — so the window is filtered by its end.
- Scoped by `ViewLive`: that a camera is alarming is part of its live state, like the boxes over its
  picture. Each alarm carries `archive` — whether the caller may also watch its recording.
- A running alarm puts a breathing red ring on the camera, wherever it is drawn (where it stands and
  its pictures alike); one that ended lately, a faint ring. It also lights every space it is drawn
  in **and every space those are nested in**: the outline of a building on the site map burns when
  a camera on any of its floors alarms, and the floor switcher and the breadcrumbs show a dot.
- The list beside the map starts with the alarms; picking one opens the space the camera is drawn
  in (this one if it is here, otherwise where it really stands) and selects it. The camera's panel
  then says since when and why, and offers the recording of the alarm beside the live picture.
