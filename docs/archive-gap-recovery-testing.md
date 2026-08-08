# Testing archive gap detection and camera-side recovery

The feature spans four moving parts — the reader's liveness reports, the writer's segment
boundaries, the outage table in VmsFs, and the import queue that pulls the missing stretch off the
camera's SD card. Every one of them is about *timing*, and none of it can be verified by reading:
the defects found so far were all boundaries that were off by seconds, in code that looked correct.

This is the procedure for exercising it against a real camera.

---

## Prerequisites

**Infrastructure** (docker compose, must be up first): Postgres with `ta_cfg` and `ta_fs`, Redis,
MediaMTX, dapr placement. The dev stack joins Square's shared network, so `multitenant_admin` has to
be running — see `CLAUDE.md`.

**Camera.** A device with ONVIF Profile G (Recording / RecordingSearch / Replay) and an SD card.
Reference device: FIS `F-IC-2642C2MSZ4` at `192.168.2.64`, roughly an hour of on-board archive. The
NVT at `192.168.1.150` has no Profile G and serves as the negative test.

**Configuration.** `camera_setup.edge_backfill_stream` must name the stream, or gaps are recorded but
never recovered. The stream's recording profile decides what "expected to be recording" means, which
is what several scenarios below turn on.

**Stack.** `scripts/dev-up.sh` starts all nine services with their sidecars, `scripts/dev-down.sh`
stops them, `scripts/dev_stack.py restart <service>` cycles one, `status` shows what is up. Ports,
environment and daprd flags come from `.vscode/launch.json` and `.vscode/tasks.json`, so the scripts
and F5 cannot disagree and can be mixed freely.

## Driving the system without the UI

**Change the recording mode.** Set `camera_stream.recording_profile_id` in `ta_cfg` (builtin
profiles: `…0001` continuous, `…0002` prealarm, `…0003` off), then send the signal VmsCfg would have
sent, and media_server refetches the configuration itself:

```bash
curl -X POST localhost:3600/v1.0/publish/orderpubsub/media-server-config-changed \
  -H 'Content-Type: application/json' -d '"vms"'
```

**Raise an alarm.** Call `FsService.RetainEvent` on VmsFs (`:5137`) — that is exactly what VmsOnvif
does when an armed camera matches an alarm filter. It activates the continuous override only when
the stream's base profile is `off`.

**Break the link.** `iptables -I OUTPUT -d <camera-ip> -j DROP` plus the same on `INPUT`, then delete
both. Wrap it so the rules are removed even if the shell dies — a forgotten DROP rule looks exactly
like a broken camera later.

**Kill the media server.** `kill -9` on the `vms` process. Not a graceful stop: a clean shutdown
sends a farewell report, and the crash path — where nothing is reported and the interrupted segment
is left unmeasured — is the one that has been wrong twice.

## Where to look

| What | Where |
|---|---|
| Outage boundaries, state, progress | `ta_fs.source_outages` |
| Import queue, leases, attempts | `ta_fs.edge_import_jobs` |
| Coverage; `origin='edge'` is recovered footage | `ta_fs.clips` |
| Decisions and corrections | VmsFs log — `Source outage opened`, `Measured N clip(s)`, `Gap start … corrected to`, `Edge backfill:` |
| When delivery actually stopped | media_server log — `DIAG rtsp_reader: video source gap=` |
| Ground truth for a clip's real length | `ffprobe -v error -show_entries format=duration -of csv=p=0 <file>` |

---

## Scenarios

### 1. Clip and outage boundaries

Nothing below means anything while the boundaries lie, so this goes first.

**Do:** with recording on, break the link for ~70 s, then restore it.

**Expect:** the open segment closes on the last frame that actually arrived, not when the loss was
noticed. `source_outages.started_ts` equals the previous clip's `end_ts` and `ended_ts` equals the
next clip's `start_ts`, to the millisecond — the heartbeat period (15 s) must not show up in either
boundary. The clip's `end_ts − start_ts` matches its duration by `ffprobe`.

### 2. Link loss while recording continuously

**Do:** the same outage, followed through to the end.

**Expect:** no row until `VMS_SOURCE_OUTAGE_GRACE_SECONDS` (15) has passed; `cause = 'link_lost'`,
not `server_down`, because the node's other streams keep reporting. Then `open` → `filling` →
`filled`, with `edge` clips covering the whole interval plus the deliberate ±1 s margin, and
`filled_ms` equal to the interval length.

### 3. Media server crash

**Do:** `kill -9` the `vms` process, wait past `VMS_SOURCE_LIVENESS_STALE_SECONDS` (60) so the
sweeper opens the row, then start it again.

**Expect:** `cause = 'server_down'` (every stream of the node silent at once). The segment that was
being written is measured from its own fragments rather than left with a null `end_ts` — an
unfinished clip is read as covering the next minute, which is precisely the stretch that was lost.
The gap then starts where that clip really ends, not at the last heartbeat, which on a crash is up
to a full heartbeat period stale. One row per crash, never two.

Two defects lived here, both fixed on 2026-08-08: the finaliser skipped any segment opened *after*
the last heartbeat (with 5 s pre-alarm segments, the usual case), and the archive-derived boundary
was discarded because the new run's first clip starts on exactly the frame used as the query bound.

### 4. Recording driven by an alarm

**Do:** set the base profile to `off`, arm the camera, fire `RetainEvent`, confirm recording starts,
then break the link.

**Expect:** the override becomes continuous and recording starts on the first keyframe — with no
pre-alarm buffer, roughly a second passes and nothing before the alarm exists to keep. The gap is
created because the alarm made recording expected, not a schedule. It is then recovered normally.

### 5. Pre-alarm — the common production mode

**Do:** set the base profile to `Pre-alarm` (5 s segments, 5 s before and after), break the link.

**Expect:** short segments are written continuously; boundaries are as exact as in the other modes.
Live segments that no alarm covers are removed by `ClipCleaner` within about a minute, but imported
footage is ingested with `force_retained` and survives — recovering a gap must not be undone by
retention.

### 6. False positives — rows that must **not** appear

| Do | Expect |
|---|---|
| Break the link with recording off | no row at all |
| Break the link for less than `GRACE` | no row; repeated blinks extend one row rather than minting thousands |
| Edit the camera config so the stream is recreated | no row |
| Remove the camera from the configuration | the open row closes, no new ones |
| Turn recording on after a long idle period | no gap invented retroactively |

### 7. The import queue

**Expect:** one row per interval — re-queueing the same stretch does not duplicate it; a job is
claimed by exactly one runner (`FOR UPDATE SKIP LOCKED`); an expired lease makes it runnable again,
so a restart mid-pull resumes instead of starting over; cleanup removes finished jobs and never
touches `queued` or `running`. Gaps are taken oldest first and one camera at a time — the device has
very few RTSP sessions.

### 8. Exhaustion

**Do:** ask for a stretch the card demonstrably no longer holds (older than `DataFrom`).

**Expect:** `exhausted`, and it is terminal — a ring buffer never gets its old material back. It
takes both age and a repeat: a camera in trouble answers a search with nothing at all, which is a
statement about the network, not about the card.

### 9. Operator against automation

**Do:** during a running job, try the manual controls; cancel; then return the gap to automation.

**Expect:** manual controls blocked while `filling`, with cancel offered; cancel leaves the gap in
`held`, which the scheduler will not pick up while manual start stays available; "return to
automation" puts it back to `open`.

---

## Timings

Waiting less than these produces confusing results, not faster tests.

| Step | Wait |
|---|---|
| Outage materialises after the link drops | `GRACE` (15 s) plus a heartbeat |
| Outage materialises after a media server crash | `STALE` (60 s) plus the sweeper's 15 s tick |
| Outage closes after recovery | within a second, on the first frame back |
| Scheduler leaves a fresh gap alone | `VMS_BACKFILL_SETTLE_SECONDS` (60) after it closed |
| Sweep interval | 30 s, and a queued job is claimed on the *next* sweep |
| Unblock → finished import | allow ~2 minutes |

The camera also needs about a minute before a just-recorded stretch becomes replayable, which is
what the settle window is for; retries are spaced, never back-to-back.

## Not covered yet

- Audio on import — the reference camera's `AudioEncoderConfiguration` is not bound to `Profile_1`.
- Manual import still bypasses the queue, so the "one path for everyone" property is untested.
- The `ignored` state has no endpoint or control.
