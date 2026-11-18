# States, events and rights on the map

> Part of [Maps & plans](../maps-and-plans.md): the model and where things live are there.

## States, events and rights

- **Every camera is a node from creation.** The adapter in `VmsMap/Adapter/` creates it from
  `server-config-changed`, always under its server's node; `device_node` maps the node back to the
  camera id (and, for parts, to the part's token). A camera removed from VmsCfg takes its node,
  replicas, placements and observations with it.
- **A device's rights are anchored to its reference replica's space.** A canonical node under the
  server does not inherit a right granted on the space the camera is drawn in, so a camera node and
  its parts are also checked from the reference replica's path; with none, the server node's path is
  all there is.
- **A retention episode becomes `ALARM` plus one journal event**, and `NORM` comes back when a
  sweep confirms the episode is over; `CONNECTED`/`DISCONNECTED` follow the open source outages in
  VmsFs. The alarm counters bubble up `alarm_states` in the same transaction. The event's id is the
  episode's `event_uuid`, so a redelivered message writes nothing twice.
- **An alarm reaches the room the camera stands in.** The canonical tree ends at the server, so the
  counters alone would stop above the camera. A camera whose row changes has that row mirrored onto
  its **reference replica** — the picture on the floor plan — and the spaces above it recount; a
  picture's row is never counted as a second alarm, and only the reference replica gets one. When a
  placement moves the reference, the old picture's row goes and the new one's appears.
- **Rights are rows on tree nodes** (`rights(object_id, subject, action)`), inherited downwards;
  `admin` bypasses them, and one open/enforced switch decides what a node with no rows means.
  VmsMap computes one user's effective set and applies it to every multi-node read; web_srv's
  `RightsService` caches it per user and drops it on `rights-version`.
- **`api/map/live`** is SSE over `MapService.Watch`; the client sends a box or a set of ids, gets
  the changed ids back and fetches them over `api/map/*`. The subscription is in the request, so a
  change of view opens a new stream — Dapr proxies server-streaming but not a bidirectional call.
  A box scope also states its completeness: `set_box_truncated` (`{"truncated": true|false}`, no
  items) is sent on the first poll and whenever the answer changes, because the box read is capped
  at 10 000 figures; an id scope lists its nodes explicitly and never sends it.


## Alarms on the map

An alarm here is what the rest of ta_vms already calls one: a retention episode in VmsFs, opened
and renewed by `RetainEvent` (VmsOnvif for a matched ONVIF event, VmsAnalyticsSingleton for an
analytics detection). Its `alarm_end_ts` is always set — the projected end every renewal pushes
forward — so an alarm is **running** while that is in the future, and there is no such thing as one
left open by a crash.

- M3 makes the map's copy of an alarm a state on the camera node (see "States, events and
  rights"), but the operator's screen keeps polling `GET api/fs/alarms` until M4 moves it onto
  `api/map/live`.
- The operator's map polls `GET api/fs/alarms` every 3 s while it is open: alarms running now and
  those that ended in the last 15 minutes, asked of every VmsFs by end (`ending_after`) rather than
  by start, so one that started an hour ago and is still being renewed is found. web_srv keeps no
  subscriptions, so the map asks; the request is one small fan-out.
- Scoped by `ViewLive`: that a camera is alarming is part of its live state, like the boxes over its
  picture. Each alarm carries `archive` — whether the caller may also watch its recording.
- A running alarm puts a breathing red ring on the camera, wherever it is drawn (where it stands and
  its pictures alike); one that ended lately, a faint ring. It also lights every space it is drawn
  in **and every space those are nested in**: the outline of a building on the site map burns when
  a camera on any of its floors alarms, and the floor switcher and the breadcrumbs show a dot.
- The list beside the map starts with the alarms; picking one opens the space the camera is drawn
  in (this one if it is here, otherwise where it really stands) and selects it. The camera's panel
  then says since when and why, and offers the recording of the alarm beside the live picture.
