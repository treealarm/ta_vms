-- Durable queue of camera-side imports.
--
-- Until now a run lived only in the scheduler's process: a restart mid-pull left the gap marked as
-- being filled with nothing behind it, and the work had to be rediscovered by timing out. The queue
-- makes the unit of work a row, so a restart resumes it instead of losing it — and makes room for
-- the manual button to use the same path rather than driving the media server on its own.
CREATE TABLE IF NOT EXISTS edge_import_jobs (
  job_uid        uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
  camera_id      text        NOT NULL,
  stream_name    text        NOT NULL DEFAULT '',
  range_start_ts timestamptz NOT NULL,
  range_end_ts   timestamptz NOT NULL,
  -- The gap this serves. Kept when the gap is cleaned up: the job's own history is still readable.
  outage_uid     uuid        NULL REFERENCES source_outages(outage_uid) ON DELETE SET NULL,
  state          text        NOT NULL DEFAULT 'queued',  -- queued|running|succeeded|failed|cancelled
  attempts       integer     NOT NULL DEFAULT 0,
  message        text        NOT NULL DEFAULT '',
  -- Held by whoever is running it. A lease in the past means the runner is gone and the job is free
  -- again — that is the whole point of the table.
  lease_until    timestamptz NULL,
  -- The media server's own job id while running, so a resumed scheduler can find its work again.
  media_job_id   text        NOT NULL DEFAULT '',
  -- Read by the cleaner below; there is no other reason to keep a finished job around.
  updated_ts     timestamptz NOT NULL DEFAULT now(),

  -- One job per stretch of a gap: re-planning the same piece must not queue it twice.
  UNIQUE (camera_id, stream_name, range_start_ts, range_end_ts)
);

-- The scheduler's only query: what is free to run.
CREATE INDEX IF NOT EXISTS idx_edge_jobs_runnable
  ON edge_import_jobs (camera_id, range_start_ts)
  WHERE state IN ('queued', 'running');

-- The cleaner's query.
CREATE INDEX IF NOT EXISTS idx_edge_jobs_finished
  ON edge_import_jobs (updated_ts)
  WHERE state NOT IN ('queued', 'running');
