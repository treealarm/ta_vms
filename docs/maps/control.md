# Control surface: the objects an operator acts on

> Part of [Maps & plans](../maps-and-plans.md): the model and where things live are there.

## Control surface

The operator's **Control** tab (`/operator/control`) lists the objects an operator acts on in a
space and keeps their actions beside the selected one: `GET api/control/objects?spaceId=&kind=&cameraId=&pageSize=&afterName=&afterId=`,
backed by `MapService.ListControlObjects`. Three kinds share one row, and each answers with what
its own action writes:

- **camera** (`part_kind='device'`) — arming answers the same scheme as a sensor: its own mode in
  the map (`device_object_settings`, `inherit` by default), else the zone above its reference
  replica, else off. An operator's toggle (`PUT api/cameras/{id}/state`, `SetDeviceArming`) is an
  explicit command; who decides — the zone or the card — is the administrator's setting, and
  `inherit` hands the flag back to the zone.
- **digital input** — the effective arming of `EffectiveArmingResolver`, its configured mode and
  alarm flag, and the contact the camera reports (`SENSOR_OPEN`/`SENSOR_CLOSED` on the part node).
- **relay** — never armed: a relay is driven, not watched. Its action is its state
  (`POST api/DeviceIo/.../relay-outputs/{token}/state`), and its row shows what the camera reports
  (`RELAY_ACTIVE`/`RELAY_INACTIVE`).

**A container is guarded as a whole.** A space — or any node a camera's reference replica actually
stands through — has a row in `object_arming`, written by `SetNodeArming`
(`PUT api/control/objects/{id}/arming`, modes `armed`/`disarmed`/`inherit`). The row is the node's
own decision (`armed` boolean); `inherit` is its absence. It is the map's typed state, not one of an
object's free-form properties: a property was editable on the object's form, without the journal
entry and the alarm re-check an arming change owes, and the migration
(`0002_object_arming.sql`) moved every `properties.armed` row into it. The zone is **not**
written onto the sensors: each one resolves it, and an alarm names the zone that settled its device
(`armingSourceObjectId`). A container nothing stands through — a diagram of a camera's parts, a
decorative figure, the server node — is not armable, because a property there would be read by
nobody. Arming a container writes the row, the journal entry (`arming`, with the operator) and
an outbox row in one transaction; the outbox row is what makes `SensorAlarmService` re-check the
sensors already in their trigger position, so a crash between the commit and the re-check loses
nothing. Disarming closes no alarm — only the condition leaving does.

**An explicit off inside an armed zone is a bypass.** A device or container whose own row is
`disarmed` while the nearest explicit row above it is `armed` is reported as `bypassed`: it is not
simply off, it is excluded from a zone that still guards the rest. The answer does not change
`armed` — the node's own row settled that — and `source_object_id` names the zone it is excluded
from, so its open alarms stay in that zone's group and the zone's "Acknowledge all" keeps reaching
it. The flag is derived on every read; a zone going on or off guard changes it with no write to the
node.

A bypass may carry a deadline (`bypass_until`, with the own mode to restore at it). The sweep in
VmsMap restores that mode when the deadline passes and journals the automatic action as `system`;
a contact still closed raises an alarm at that moment, which is the safety net against a forgotten
exclusion. Extending a deadline keeps the original intent: an armed room goes back to `armed`, a
node that followed its zone to `inherit`. The deadline lives on the same row as the decision, so an
explicit mode change cancels it. The action is ordinary `control`; there is no separate right, and
the journal is the existing `arming` entry with the deadline and `expired` in its extra properties.

The exclusion is set on the node the operator works with. Setting a floor or a zone excludes every
device under it that still inherits; a device with an explicit mode keeps it. Nothing is written
onto the sensors.

**The effective flag is written back to VmsCfg.** `camera_states.armed` is read by services that
cannot see the map — VmsOnvif's ONVIF retention gate and web_srv's live state — so the map writes
the value the camera's mode or its zone implies: on a zone's change, when the camera's reference
replica moves or goes, when a node is moved in the tree, when the camera's own state or settings
change, and in a sweep at startup over the zones that are set. The map is the only writer and it
writes for an explicit mode as well as an inherited one; the write happens only when the value
differs (`UPDATE … WHERE armed IS DISTINCT FROM …`), so the event a write announces cannot feed
itself.

**The settings live on the node.** `device_object_settings` (VmsMap) holds the arming mode of every
camera and input node and, for a digital input, `trigger_on`/`alarm_enabled`. The old columns —
`camera_states.arm_mode` and the same three in VmsCfg's `device_settings` — are a migration source
for this release only: the map's one-time import copies them before the first flag derivation, and
VmsCfg refuses the writes. An administrator edits them through one card, the same for a camera and
an input (`Inherit / Armed / Disarmed`, plus the alarm rule for an input), shown wherever the object
is selected — Admin → Nodes and the admin map's inspector (a camera or a part as its picture,
resolved through its owner). The camera's page is about the hardware: its Main tab points there,
and its Devices tab keeps the alarm rule beside the contact's polarity, because that is what the
rule is about. The operator's switch offers the same three modes: an explicit on or off is the
operator's own decision and outranks the zone the device stands in, and `inherit` is how that
override is taken back.

**One arming scheme.** `EffectiveArmingResolver` answers what a sensor resolves to, and where the
answer came from: the part's own `arm_mode` (`armed`/`disarmed` → `explicit`), otherwise the nearest
`object_arming` row above the camera's **reference replica** (`space`), otherwise the camera's own
effective flag (`camera`). For a camera the walk ends at its own mode → zone → off: its flag is
what the walk produces, so reading it back as an input would turn the derivation into a loop.
`inherit` is a command like the other two — whoever sets an explicit mode can hand the node back
to its container.
For a container itself the walk starts at its own node (`explicit` / `inherited` / `none`), which is
what the browser paints differently: green for armed here, purple for armed through an ancestor,
grey for not armed. The scheme is the same for one card and for a page of the panel — the page
resolves in one pass, with the settings and the camera flags read in bulk.

**The list is server-side.** It is the tree's own read, not a browser enumeration:

- `space_id` covers the space **and everything under it**, which is the set the space's own `armed`
  property reaches; an empty id asks for everything the caller may see. A zone will arrive as a
  polygon in the same request, not as a second list.
- `kind` narrows to `camera`, `digital_input` or `relay`; `camera_id` answers one device's objects
  wherever they stand — the administrator's placement palette asks this way.
- Each row is addressed by `source_kind`/`source_id`/`part_kind`/`part_token`, never by camera:
  a fire panel and its loops will join the same list under a kind of their own.
- Rows are filtered by the caller's `view_live` before the page is cut, and `can_control` says
  whether an action may be used. The page is a keyset one — ordered by the object's name, `has_more`
  plus a cursor, no total — because rights can hide rows and an offset would shift the list every
  time somebody arms something.
- A camera with no reference placement stands nowhere: a space filter leaves it out, the
  unfiltered list still shows it.
- No write path is added. The actions are the commands that already write — the camera flag, the
  sensor's `SetDeviceArmMode`, the relay's state.

**One object by any of its nodes.** `GET api/control/objects/{objectId}` answers the same row for a
canonical node or a replica of it, resolving the replica through `objects.owner_id`. The operator
map's inspector uses it, so selecting a sensor or a relay — a picture, a diagram region, or the node
itself — offers the same actions as the panel. A node the caller may not see answers not-found,
exactly as if it did not exist. A camera needs no such section there: its card arms it already, and
a second switch would be two ways to say one thing.

**Grouped by kind, and a relay is switched where it is listed.** In "All" the list is not one
heap: cameras, sensors and relays are sections of their own, because a reader scans one kind at a
time. A relay carries its switch in the list row as well as in the pane, with the state beside it;
the panel re-reads its pages every few seconds, so a change the panel did not make — another
operator, a rule — appears without an action.

**A relay command is a wish until the camera confirms it.** ONVIF has no getter for a relay's state
and a camera may report the change only when its event subscription next delivers one, a minute on
some devices. VmsOnvif therefore keeps the commanded value apart from the camera's report and shows
the wish until the camera says the same state; a report repeating an older value while the wish
stands is not a transition and does not turn the switch back. The wish is repeated until the camera
confirms it, and dropped after its lifetime (a monostable relay's `delayMs` plus a margin, otherwise
20 s) with the camera's own report back on screen and a warning in the log. The card and the ONVIF
settings pages mark a value that is still a wish; the Control list does not — VmsMap knows nothing
of the wish and shows the value the command produced.

**The tree reads a part's state, it does not drive it.** A part's row in the operator map's tree and
in Admin → Nodes carries a state dot from the same `states` the Control panel reads, and no switch:
one place drives a relay, and a second switch would be a second path that could disagree with the
first. The dot is amber when the device no longer reports the part (`present=false`), even though
the row has no contact state left; that is read from the node too, and the fault is settled in the
alarm list, not from here. Admin → Nodes follows the map's watch stream, so a relay switched in the
panel changes there without reopening the row.

**The card keeps its switch.** The camera card's Device I/O overlay arms a sensor on a line of its
own, under the caption "Guard", beside the effective state — not on the token line beside the live
0/1, where it read as changing what the sensor says. Arming from the monitor is ordinary operator
work; the panel is where a whole space is worked through.
