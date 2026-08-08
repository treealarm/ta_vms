-- Intervals in which a camera's video source was silent, derived from media_server heartbeats.
--
-- One table on purpose: a heartbeat gap carrying "recording was supposed to be running" IS an
-- archive gap, so there is no second table of gaps, no clip-based scanner and no retro-computed
-- expectation. The flag is observed at the time, which is strictly better than reconstructing it —
-- override and alarm history is not stored anywhere and could not be reconstructed at all.
CREATE TABLE IF NOT EXISTS source_outages (
  outage_uid  uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
  camera_id   text        NOT NULL,
  stream_name text        NOT NULL DEFAULT '',
  -- Last frame actually received. Never the moment the silence was noticed: that would put every
  -- boundary up to one heartbeat period late and make a backfill re-pull footage we already have.
  started_ts  timestamptz NOT NULL,
  -- NULL while the source is still silent. The only rows the cleaner never removes.
  ended_ts    timestamptz NULL,
  cause       text        NOT NULL,   -- 'link_lost' | 'server_down'
  recording_expected boolean NOT NULL DEFAULT false,

  -- Backfill lifecycle. Meaningful only for recording_expected = true.
  --   open | filling | filled | exhausted | partial | held | ignored
  -- exhausted = the camera confirmed it no longer holds the footage. Irreversible: the card is a
  -- ring buffer, material only ever disappears from it.
  state         text        NOT NULL DEFAULT 'open',
  -- Display cache, recomputed after each job. NOT a source of truth: what is actually covered is
  -- decided from the clips table, because retention can delete patched footage and this would lie.
  filled_ms     bigint      NOT NULL DEFAULT 0,
  -- Consecutive fruitless attempts; reset on any progress, so a long gap patched piece by piece
  -- does not exhaust its budget precisely because it is succeeding.
  attempts      integer     NOT NULL DEFAULT 0,
  next_retry_ts timestamptz NULL,
  message       text        NOT NULL DEFAULT '',

  UNIQUE (camera_id, stream_name, started_ts)
);

CREATE INDEX IF NOT EXISTS idx_source_outages_camera_time
  ON source_outages (camera_id, stream_name, started_ts DESC);

-- "Is this stream silent right now" — also what the tracker reads on startup to rebuild its state.
CREATE INDEX IF NOT EXISTS idx_source_outages_open
  ON source_outages (camera_id, stream_name) WHERE ended_ts IS NULL;

-- The scheduler's only query. A closed gap drops out of it for good, so nothing ever rescans
-- everything.
CREATE INDEX IF NOT EXISTS idx_source_outages_pending
  ON source_outages (state, next_retry_ts)
  WHERE state = 'open' AND recording_expected;
