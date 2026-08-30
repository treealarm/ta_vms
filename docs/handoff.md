# Where the work stands

For whoever picks this up next — another person, another agent. Written 2026-08-26, on
branch `tz-v2`.

## Read these first, in this order

1. **`CLAUDE.md`** at the repository root. Architecture rules that override defaults: which
   service may talk to what, how configuration changes propagate, what scales by replicas and
   what by sharding, where deployment lives. Not optional background — several of them are
   load-bearing and a change that ignores them will be wrong in a way tests do not catch.
2. **`docs/`** — this folder. Prose documents on specific machinery, each written when
   something turned out to be non-obvious. `test-camera-from-file.md` is the one to read before
   touching anything about cameras, streams or analytics: it explains the camera that plays a
   file, which is how the system is exercised without hardware.
3. **`roi_transcode/docs/plan-v2.md`** — the encoder subproject's plan against the customer's
   specification, with the decisions taken and why. `roi_transcode/docs/COLLABORATION.md` holds
   the baton for that plan; it is shared with a second person and has its own rules.

Chat history is not shared state. Anything that must survive a handover lives in these files.

## What was done, and what it cost to find out

A day went into one symptom: the operator's live view froze for whole seconds whenever
analytics was running. The chain of elimination is worth knowing, because several plausible
answers were wrong and each was disproved by measurement rather than argument.

**The archive was fine while the live view was not**, which put the fault after the reader and
in the WebRTC branch alone. In the browser: `packetsLost 0`, `framesDropped 0`, jitter 2 ms —
and `pliCount` climbing. A keyframe request without a single lost packet means packets arrived
*late*, not missing; MediaMTX renumbers RTP per reader, so an upstream gap is invisible to a
loss counter.

Disproved along the way: the audio track and its transcode, raw bitrate on its own, the area
budget, our own queue sizes, MediaMTX's `writeQueueSize`, kernel socket buffers, the analytics
worker being slow to drain (both readers were measured receiving bytes in lockstep), reconnect
churn, CPU starvation, and the bbox overlay's rendering cost.

Two things were actually responsible:

**A detector and an operator reading the same high-bitrate path.** MediaMTX pushes one keyframe
burst — about 1.5 MB, a thousand packets — to both readers in turn, so the browser's share
arrives spread over twice as long and misses its 46 ms frame-assembly window. Fixed by having
media_server publish to the worker directly (`EnsureAnalyticsPush`), which also ended the
absurdity of analytics forcing the WebRTC push on just to read the relay back.

There are now two ways video reaches the detector without a media router in between, and they
are not alternatives. A **live camera** uses the push above: its video exists only inside the
process holding the socket, so there is no file to hand over. An **offline pass over a corpus**
sets `WatchRequest.file_path` instead and the worker opens the file itself; packing a clip into
a stream just to feed the detector would add a link with nothing to test in it. A watch has one
source, so the two fields are never set together.

**Docker Desktop.** Its LinuxKit VM runs the containers behind QEMU user-mode networking plus
vpnkit, so every packet crosses a userspace network stack. It costs a steady core and a half
(`qemu` ~140%, `com.docker.back` ~30%) regardless of load, and it is where the bursts died.
Moving to the native daemon made the freezes disappear at a stroke. **This is a requirement for
the development environment, not a preference** — the same file already warns that Docker
Desktop has no `/dev/dri` for the GPU; the networking cost belongs beside it.

Three real defects surfaced on the way and are fixed: the file reader restarted its output
timeline on every loop, packing 1861 frames into 1.12 seconds of timeline; libavformat's default
RTP payload made MediaMTX re-fragment every packet; and a listening socket that timed out was
never released, so every retry after the first failed with the address in use.

## Capacity, measured

Nine and sixteen cameras, all watched at once, on this machine:

| cameras | bitrate | traffic | `vms` | MediaMTX | picture |
|---:|---:|---:|---:|---:|---|
| 9 | 8.75 Mbps | 79 Mbps | 15% | 13% | freezes |
| 16 | 8.75 Mbps | 140 Mbps | 27% | 66% | bad freezes |
| 9 | 2.56 Mbps | 23 Mbps | 5% | 26% | clean |
| 16 | 2.56 Mbps | 41 Mbps | 20% | 42% | clean |

The count is not the limit; the stream size is. media_server is linear and cheap throughout —
sixteen streams for a fifth of one core, 114 MB resident. Reading files costs almost nothing:
the expensive stage is the detector, and only because a clip whose keyframes are 5.27 s apart
forces it to decode every frame. At a camera's usual one-second GOP it decodes keyframes alone,
which is thirty times less.

## Open threads, most useful first

**Sub-streams for the wall.** The measurement above is the argument: no VMS sends main streams
to a grid. `analytics_watch.stream_name` and per-camera streams already exist, so the pieces are
there; what is missing is the rule "sub in the grid, main when expanded". This is the answer to
"does it handle 16 or 32 cameras", and without it the honest answer is "not like this".

**The analytics worker dies silently.** Twice in one day, after roughly eighteen minutes and
four thousand detections, with no message and nothing in the kernel log. Track ids were past
2500 by then, so the tracker or the re-identification gallery are where to look. Unrelated to
everything above, and a real defect.

**The ROI re-compression operating point.** Measured on real clips: the current defaults make
the file 30% *larger* than a plain re-encode, and the `no_gain` guard is all that protects the
archive. `roi_max_coverage` still defaults to no ceiling; there is now a number to justify one
(0.70 protected share on a crowded clip against 0.03 on a sparse one). See the tail of
`plan-v2.md`.

**Section 13's remaining half.** `spatial_smoothing`, `temporal_smoothing`,
`max_qp_delta_per_frame` and `use_track_prediction` are declared and refused rather than
silently ignored. `max_qp_delta_per_frame` is the interesting one: the area budget made a
region's delta depend on what else is in frame, so a face appearing can flip a person from −7 to
0 between frames.

**Documentation debt.** The Docker Desktop finding and the sub-stream rule both belong in
`docs/`, and neither is written yet.

## Things that will waste your time if nobody says them

- The analytics worker needs its own `daprd` sidecar, or `VmsAnalyticsSingleton` cannot reach it
  and no watch ever starts. The symptom is a healthy-looking worker that never receives
  `StartWatch`.
- `ANALYTICS_ADVERTISE_HOST` in `video_a/.env` is `analytics-worker`, a compose service name. Run
  the worker on the host and media_server cannot resolve it; use `127.0.0.1`. With the variable
  unset the code falls back to the machine's own hostname, which is right in both places.
- The worker listens on **8555**, not 8554: a media router already answers on 8554 and the bind
  would simply fail where both run on one host.
- The monitor's camera list lives in **`monitor_slot`**, not `monitor_camera`. Writing to the
  latter looks like it works and changes nothing on screen.
- `scripts/dev_stack.py` and F5 must not be mixed — they fight over the same sidecar ports. The
  script's own header says so.
- Configuration written straight into `ta_cfg` needs the `server-config-changed` event published
  afterwards, or nothing notices: the caches that hold camera setup have no TTL.
  `scripts/set-file-camera-clip.sh` does both and is the shortest example.

## The other repository

`video_a` (the analytics engine) is a separate checkout and a separate repository. Today's work
there is on branch **`adaptive-sampling`**, pushed but not merged: the frame sampler that decides
between keyframe-only and full decode by measuring the stream, and the listen mode that lets
media_server publish to it. Its `frame_sampler.h` explains the reasoning; the two repositories
have to move together for the direct feed to work.
