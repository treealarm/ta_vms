# Camera connection state: an event instead of a poll

The map shows `CONNECTED`/`DISCONNECTED` on a camera node. It gets them by polling each node's
VmsFs (`ListSourceOutages`, one call per node every five seconds) and treating an outage without
an `ended_ts` as `DISCONNECTED`. That works, but it is a poll of state that video_srv already knows
at the moment it changes.

**Why not MediaMTX paths.** The first implementation asked VmsUtils for
`GetMediaMtxPathsInfo`. `video_srv`'s `mediamtx_monitor` stops a WebRTC path after 30 seconds with
no viewers, so an absent path means "nobody is watching", not "the camera is gone". On the bench it
marked all four cameras `DISCONNECTED`, and `DISCONNECTED` is seeded as an alarm state. The path is
a viewer-demand signal, not a source-liveness one.

**What is still open.**

- video_srv already computes `source_alive` per stream and reports it to VmsFs every 15 seconds.
  Publishing a `camera-connection-changed` topic (camera id, stream, alive) at the transition would
  let the map react at once and drop the poll.
- VmsFs's `SourceLivenessTracker` also opens an outage only when `recording_expected` is true. On
  the OnvifSim bench a camera that was recording did not produce an open row after its network was
  cut, so the map stayed `CONNECTED`; the adapter consumes rows correctly (tested by inserting one),
  but the producer's expectation flag needs a look.
- The event should be addressing only, like `retention-event-created`; the map re-reads what it
  needs. Do not put the state in the event as authoritative data.
