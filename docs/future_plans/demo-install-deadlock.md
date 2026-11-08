# The demo install can deadlock against the device-event path

Found 2026-11-08 while verifying the ONVIF pull-point fix on the bench (nothing to do with it).

`POST /api/map/admin/demo/install` — the administrator's button, and what `tests/demo/check_demo.py`
calls — sometimes answers 502, and VmsMap's log carries PostgreSQL `40P01 deadlock detected`, with
the stack ending in `DemoSeeder.InstallAsync`. Two of about eight runs on that bench failed that way;
running the same script again succeeded, and the world was installed correctly the next time. The
whole install is one transaction, so a failed attempt leaves nothing half-written.

## What is established

- The install holds `MapLocks`' demo advisory lock and writes the world's nodes, placements, types
  and states in one transaction (`dotnet/VmsMap/Demo/DemoSeeder.cs`).
- It runs while device events are being journalled: the bench's simulator changes relay and input
  states continuously, and VmsOnvif publishes them (`device-event` → the map's states/journal).
- Both paths write the same `ta_map_*` rows, and the M2.4 rule (every writer takes
  `ta_map_camera_state` before `ta_map_alarm` through `MapLocks`, never the other way) exists
  precisely because the reverse order deadlocks.

## What is still open

Whether the seeder joins the state/alarm path in a different order than a journal write, or whether
one of its bulk updates should be split. Whatever the fix is, it has to keep the M2.4 ordering rule,
which is checked by the grep test rather than by memory
(`docs/plans/square-merge/m2.4-devices-alarms-automation.md`, item 10).

Until then: a demo install that answers 502 with `40P01` in VmsMap's log is this, not a broken
world — re-run it.
