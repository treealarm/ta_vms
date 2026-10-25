# Future plans

Findings that need doing, but not next: each file is one topic, written when it was found, with
what was established at the time and what is still open. They are not commitments and not a
backlog in order; they keep what was learnt from being lost between sessions.

When work on a topic starts, its plan moves into the work itself (a plan file, a PR) and the file
here is deleted or cut down to what is still left.

| File | Topic |
|---|---|
| [distributed-analytics.md](distributed-analytics.md) | Re-identification and archive search in the database (pgvector), then analytics per node |
| [distributed-hardening.md](distributed-hardening.md) | What a multi-machine deployment still leaves open on the network |
| [log-volume-leftovers.md](log-volume-leftovers.md) | What still fills the logs after the 2026-10-25 fix: a dead camera re-resolved per call, reconnect errors, web_srv outside VmsLog |
| [streaming-subscription-recovery.md](streaming-subscription-recovery.md) | A streaming subscription the sidecar closes cleanly goes unnoticed in Dapr.Messaging 1.18.7 |
