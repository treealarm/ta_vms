# A camera that plays a file

Running the system on footage nobody has to stand in front of — a downloaded test corpus, a clip
that reproduces a bug, the same scene twice with different settings — needs no special mode and no
new service. A camera whose stream points at a file on disk is read, recorded, published and
analysed by exactly the code that serves a real one.

This is not a stub. `media_server/reader/file_camera_reader.cpp` feeds the same `media_sink`
pipeline `rtsp_reader` does, so recording, WebRTC publication and snapshots all behave as they do
for a device. What changes is only where the packets come from.

---

## Setting one up

Three fields, and each of them matters.

**Leave the camera's IP empty.** The form writes `onvifConn: undefined` when it is blank
(`CameraSetupPanel.tsx`), which is what a camera with no device behind it should look like. See the
path table below for what happens if you fill it in anyway.

**Point the stream's path at the file**, and leave the profile token empty. With no IP on the
camera the path is taken as a local one, so it needs no prefix and no ceremony.
`media_server/cam_stream.cpp` picks the file reader when the token is empty and the path resolves to
something that exists on disk; a profile token means "ask the device over ONVIF", which there is no
device to ask.

**Switch WebRTC on for the stream.** This is the one that is easy to miss: it is what makes
media_server publish into MediaMTX, and MediaMTX is where analytics reads from. Without it the
camera records but no detector ever sees it.

Then give the stream a recording profile that is not `off`, or there will be no clips — and no
metadata either, since detections are attached to the clip that covers them.

### Where the file has to be

`docker-compose.yml` mounts `${PWD}/data` into every service as `/data` (the `x-data-volume`
anchor), so `data/testclips/scene.mp4` in the checkout is `/data/testclips/scene.mp4` to the
containers, and no compose change is needed. In dev, where media_server runs on the host under
`.vscode/launch.json`, any absolute path works.

A **directory** works too, and is read as a playlist sorted by filename, looping at the end. The
listing is refreshed as it goes, so a file dropped in while the camera runs joins the rotation
without a restart.

### The forms a path can take

`proto_mapper::resolve_stream_path` (`media_server/grpc_layer/proto_mapper.cpp`) decides, and the
camera's IP is what the rule turns on:

| `path` | camera has an IP | what happens |
|---|---|---|
| `/data/testclips/scene.mp4` | no | used as-is → file reader ✔ |
| `/data/testclips/scene.mp4` | **yes** | becomes `rtsp://ip:port//data/testclips/scene.mp4` → RTSP reader, which then fails to connect |
| `file:///data/testclips/scene.mp4` | either way | used as-is → file reader ✔ |
| `rtsp://…` | either way | RTSP reader, as always |

Anything containing `://` is passed through untouched, which is the escape hatch for the one case
the simple rule cannot express: a camera that has an ONVIF connection *and* should take its video
from a file — testing PTZ or metadata against a real device while the picture comes from disk. A
test camera has no IP, so a plain path is all it needs.

## What the file's own encoding decides

**Nothing about the detection rate any more, and this used to be the whole story.** `video_a`'s
frame sampler decodes keyframes only while the stream keys often enough to supply the requested
rate, and switches to decoding every packet when it does not. Before that, the rate *was* the
publisher's keyframe interval: a file with an 8.33 s GOP produced one detection per 8.33 s and
`sample_fps` did nothing at all. Test clips therefore need no preparation — take what you
downloaded.

What a long GOP still costs is decode: about 12% of one core for a 2688x1520 stream, against 1.6%
on the cheap path. Worth knowing before pointing forty of them at one node, and irrelevant for one.

**B-frames are worth avoiding** when you re-encode a clip for any other reason. A source without
them (which is what surveillance encoders produce) re-encoded with x264's defaults gains them, and
the reordering that comes with them showed up as visible stutter in the live view. `-bf 0` keeps
the test material shaped like the production material it stands in for.

## Why analytics needs no change

The question comes up because `video_a` reads RTSP and the source here is a file. It does not
matter: media_server publishes the file camera into MediaMTX at `{cameraId}/{streamName}`, and
`WatchReconciler.cs` hands the analytics worker exactly that URL. The worker cannot tell, and there
is no second code path to maintain.

Timing is handled on our side too. The reader paces playback against the wall clock
(`apply_playback_pacing`) and stitches timestamps across loops
(`remux_continuous_timestamps`), so the archive sees one continuous stream rather than a restart
every time the file ends.

## Checking it works

In order, because each step depends on the one before:

| What | Where to look |
|---|---|
| The file reader was chosen | `vms` log — no ONVIF connection attempt, no open error |
| Recording | `ta_fs.clips` gains rows with `size_bytes > 0` and sane durations |
| Publication | the stream appears in MediaMTX and in the live view |
| Detection | `ta_fs.clip_metadata_chunks` gains `source='cv'` rows once a watch is enabled |
| Re-compression | the clip's `roi_state` leaves `pending` once `camera_setup.roi_transcode` is on |

For the sample spacing a detector is actually achieving — the question that turned out to matter
most — the clip's own metadata answers it directly:

```sql
WITH t AS (
  SELECT DISTINCT c.from_ts + ((s->>'t')::int) * INTERVAL '1 millisecond' AS ts
  FROM clip_metadata_chunks c, jsonb_array_elements(c.samples) s
  WHERE c.clip_uid = '…' AND c.source = 'cv'
), g AS (SELECT EXTRACT(epoch FROM (ts - LAG(ts) OVER (ORDER BY ts))) gap FROM t)
SELECT count(*), percentile_cont(0.5) WITHIN GROUP (ORDER BY gap) FROM g WHERE gap IS NOT NULL;
```

## Driving it without the UI

Configuration lives in `ta_cfg` and is cached by the services that read it, so a direct write needs
the signal VmsCfg would have sent — after which each subscriber refetches for itself:

```bash
# enable ROI re-compression for one camera
docker compose exec -T postgres psql -U postgres -d ta_cfg \
  -c "UPDATE camera_setup SET roi_transcode = true WHERE camera_id = '…';"

curl -X POST localhost:3600/v1.0/publish/orderpubsub/server-config-changed \
  -H 'Content-Type: application/json' -d '{"server_id":"vms","camera_ids":["…"]}'
```

The same applies to the recording mode — see `docs/archive-gap-recovery-testing.md`, which covers
that and the rest of the without-the-UI toolkit.
