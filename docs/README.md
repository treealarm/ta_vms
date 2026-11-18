# Documentation

What lives where, and which file to read for which task. Each file stands on its own, so a task
reads the one it needs and not the rest.

Start with [`../CLAUDE.md`](../CLAUDE.md): the architecture rules that apply to every change. The
files here are the rest — the statements that matter only when you touch their area, and the
runbooks for doing something rather than changing something.

## The system

| File | Read it when |
|---|---|
| [maps-and-plans.md](maps-and-plans.md) | the map model — spaces, placements, the tree, coordinates, events; the index of the map files below |
| [maps/devices.md](maps/devices.md) | a camera reporting its own relays, inputs or mounted device; detections drawn on the map |
| [maps/control.md](maps/control.md) | the operator's Control tab and the action each kind of object answers with |
| [maps/states-and-alarms.md](maps/states-and-alarms.md) | states, the journal, rights on tree nodes, alarms and their rings |
| [maps/demo.md](maps/demo.md) | the optional demo world and how it is installed |
| [maps/calibration.md](maps/calibration.md) | PTZ landmark calibration, fixed-camera ground calibration, master/slave aiming, the algorithms |
| [architecture/distributed.md](architecture/distributed.md) | scaling a service, adding a node, anything that crosses machines |
| [architecture/ta-deps.md](architecture/ta-deps.md) | changing the C++ base image, its packages or its ffmpeg build |
| [architecture/roi-encoder-boundary.md](architecture/roi-encoder-boundary.md) | touching `roitrc`, live transcoding, `ta_roienc` or `video_a`'s worker |
| [architecture/login.md](architecture/login.md) | authentication, Keycloak, roles and rights |
| [architecture/database.md](architecture/database.md) | adding a database |
| [architecture/debug-entity-state.md](architecture/debug-entity-state.md) | the entity-state debug registry and its tab |

## Operating

| File | Read it when |
|---|---|
| [docker-bringup.md](docker-bringup.md) | building and bringing the stack up, dev or prod |
| [windows-build.md](windows-build.md) | building on Windows |
| [test-camera-from-file.md](test-camera-from-file.md) | exercising the pipeline on footage instead of a camera |
| [archive-gap-recovery-testing.md](archive-gap-recovery-testing.md) | testing archive gaps and the camera-side recovery |
| [browser-video-compatibility.md](browser-video-compatibility.md) | HLS archive playback in a particular browser |

## Work in progress

| Where | What |
|---|---|
| [plans/](plans/) | active plans, in Russian: [`square-merge/`](plans/square-merge/README.md), the map and events layer by milestone. Closed milestones and finished pieces of work are in [`plans/archive/`](plans/archive/) — history, not a task. |
| [future_plans/](future_plans/) | found while doing something else, needs doing, not next: one file per topic, in Russian, listed in [its README](future_plans/README.md). Closed topics stay in [`future_plans/legacy/`](future_plans/legacy/) as history. |
| [handoff.md](handoff.md) | a handover note of 2026-08-26 on the live-view freeze — history, not current state |

A session starts from the plan file of its task, so a plan that still carries the whole story of a
finished milestone costs that session more than the task itself. Cut it when the work lands.
