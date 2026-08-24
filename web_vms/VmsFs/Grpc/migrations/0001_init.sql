CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TABLE clips (
  clip_uid    uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
  file_path   text        NOT NULL UNIQUE,
  camera_id   text        NOT NULL,
  stream_name text        NOT NULL DEFAULT '',
  storage_id  text        NOT NULL DEFAULT '',
  start_ts    timestamptz NOT NULL,
  end_ts      timestamptz NULL,
  size_bytes  bigint      NOT NULL DEFAULT 0,
  retained    boolean     NOT NULL DEFAULT TRUE,
  -- Cached fmp4 fragment index (JSON), parsed once from the file and reused for HLS manifest
  -- builds so playback never re-reads the file to enumerate segments. Null until first parsed.
  frag_index  text        NULL,

  -- Where the footage came from: 'live' | 'edge'. A range pulled back off the camera's own
  -- storage is not a continuation of the live recording -- it was written by a different
  -- device, arrives hours later, and is allowed to overlap its neighbours by a second or two
  -- because losing frames at the seam is worse than a small overlap. Merging it into the same
  -- stretch of archive would hide all of that from the operator.
  origin      text        NOT NULL DEFAULT 'live',

  -- Re-encoding a finished clip with ROI happens at most once, and the clip is the natural
  -- place to record that: the state dies with the clip, and the candidate query reads all
  -- three columns, so none of them is speculative.
  --
  --   'pending'  nothing has been tried yet
  --   'done'     re-encoded and the file replaced
  --   'skipped'  no detection boxes for this clip, so there is nothing to protect
  --   'failed'   attempts exhausted; left alone rather than retried forever
  --
  -- Why once and not repeatedly: this is lossy-to-lossy. Quality inside the zone is preserved
  -- relative to the already-compressed clip, not to what the camera saw, so a second pass
  -- would quietly cost another generation.
  roi_state           text        NOT NULL DEFAULT 'pending',
  roi_attempts        integer     NOT NULL DEFAULT 0,
  roi_next_attempt_ts timestamptz NULL,

  -- What fraction of the clip's duration has detection boxes on it. Recorded for every clip
  -- the pass looks at, including the ones it decides to skip: the thresholds that decide are
  -- guesses until there is a distribution from real cameras to read them off, and this column
  -- is how that distribution gets collected. Null means the pass has not looked yet.
  roi_coverage        real        NULL
);

CREATE INDEX clips_camera_stream_time_idx
  ON clips (camera_id, stream_name, start_ts, end_ts);

CREATE INDEX idx_clips_camera_stream_start_retained
  ON clips (camera_id, stream_name, start_ts DESC) WHERE retained = TRUE;

CREATE INDEX idx_clips_retained_cleanup
  ON clips (retained, start_ts ASC);

-- Partial: only pending rows are ever scanned, and they are the minority once a deployment
-- has been running for a while.
CREATE INDEX clips_roi_pending_idx
  ON clips (camera_id, end_ts)
  WHERE roi_state = 'pending';

-- Two-level event identity: event_uuid = one episode (a single open->close retention cycle, the row
-- a user selects); event_id = the group label (a time-clustered session of one object, possibly
-- spanning cameras). object_id is the re-identification label of the physical object.
CREATE TABLE retention_events (
  event_uuid     uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
  event_id       text        NOT NULL,
  camera_id      text        NOT NULL,
  stream_name    text        NULL,
  alarm_start_ts timestamptz NOT NULL,
  alarm_end_ts   timestamptz NOT NULL,
  object_id      text        NULL,
  -- What set the alarm off, in the producer's own words: the ONVIF topic for an ONVIF alarm
  -- ("tns1:RuleEngine/CellMotionDetector/Motion"), NULL for anything that does not name a
  -- cause. Without it an episode records only that something happened on a camera, which
  -- cannot distinguish motion from tamper when several alarm filters are armed at once.
  trigger        text        NULL
);

CREATE INDEX idx_retention_events_camera_time
  ON retention_events (camera_id, alarm_start_ts DESC, alarm_end_ts);

-- Supports the "latest episode for (event_id, camera_id)" lookup in RetainEvent and grouped listing.
CREATE INDEX idx_retention_events_group
  ON retention_events (event_id, camera_id, alarm_start_ts DESC);

-- Intervals in which a camera's video source was silent, derived from media_server heartbeats.
--
-- One table on purpose: a heartbeat gap carrying "recording was supposed to be running" IS an
-- archive gap, so there is no second table of gaps, no clip-based scanner and no retro-computed
-- expectation. The flag is observed at the time, which is strictly better than reconstructing it --
-- override and alarm history is not stored anywhere and could not be reconstructed at all.
--
-- Only outages that cost footage are stored at all. One while recording is off -- no schedule
-- window, no arming, no alarm -- costs nothing: there is nothing to restore from the camera's card
-- because nothing was going to be written. Those rows were the bulk of the table and answered a
-- question nobody asks, so they are not written rather than written and filtered on read.
CREATE TABLE source_outages (
  outage_uid  uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
  camera_id   text        NOT NULL,
  stream_name text        NOT NULL DEFAULT '',
  -- Last frame actually received. Never the moment the silence was noticed: that would put every
  -- boundary up to one heartbeat period late and make a backfill re-pull footage we already have.
  started_ts  timestamptz NOT NULL,
  -- NULL while the source is still silent. The only rows the cleaner never removes.
  ended_ts    timestamptz NULL,
  cause       text        NOT NULL,   -- 'link_lost' | 'server_down'

  -- Backfill lifecycle.
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

CREATE INDEX idx_source_outages_camera_time
  ON source_outages (camera_id, stream_name, started_ts DESC);

-- "Is this stream silent right now" — also what the tracker reads on startup to rebuild its state.
CREATE INDEX idx_source_outages_open
  ON source_outages (camera_id, stream_name) WHERE ended_ts IS NULL;

-- The scheduler's only query. A closed gap drops out of it for good, so nothing ever rescans
-- everything.
CREATE INDEX idx_source_outages_pending
  ON source_outages (state, next_retry_ts) WHERE state = 'open';

-- Durable queue of camera-side imports.
--
-- A run used to live only in the scheduler's process: a restart mid-pull left the gap marked as
-- being filled with nothing behind it, and the work had to be rediscovered by timing out. The queue
-- makes the unit of work a row, so a restart resumes it instead of losing it — and makes room for
-- the manual button to use the same path rather than driving the media server on its own.
CREATE TABLE edge_import_jobs (
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
CREATE INDEX idx_edge_jobs_runnable
  ON edge_import_jobs (camera_id, range_start_ts)
  WHERE state IN ('queued', 'running');

-- The cleaner's query.
CREATE INDEX idx_edge_jobs_finished
  ON edge_import_jobs (updated_ts)
  WHERE state NOT IN ('queued', 'running');

-- Per-timestamp analytics geometry (bounding boxes) recorded alongside the video it describes, so
-- the archive player can draw the same boxes the live overlay draws.
--
-- Owned by the clip, not by the camera: a box is only ever useful while the frame it sits on still
-- exists, so ON DELETE CASCADE ties its lifetime to the recording. That is the whole retention
-- policy — ClipCleaner deletes a clip, the geometry goes with it, and there is no janitor to keep
-- in sync with the retention rules.
--
-- Deliberately not the `detections` table in ta_analytics: that one is debounced (one row per
-- track/kind per 10s), keyed by camera only, and carries no geometry. It answers "what happened";
-- this answers "how did it look, frame by frame". Merging them would break both.
CREATE TABLE clip_metadata_chunks (
  chunk_id    bigserial   PRIMARY KEY,
  clip_uid    uuid        NOT NULL REFERENCES clips(clip_uid) ON DELETE CASCADE,
  -- Denormalized from the clip: reads are addressed by a wall-clock window, not by a list of clips.
  -- Going through the join would mean repeating the range-limiting rules that live in ClipStorage
  -- inside every metadata query, and then keeping the two in step.
  camera_id   text        NOT NULL,
  stream_name text        NOT NULL DEFAULT '',
  source      text        NOT NULL CHECK (source IN ('cv', 'onvif')),
  from_ts     timestamptz NOT NULL,
  to_ts       timestamptz NOT NULL,
  -- One row holds one flush batch (~1s of frames), never updated after insert. Time inside the
  -- chunk is an offset in ms from from_ts, so the instant is not repeated on every frame:
  --   [{"t":0,"b":[{"i":7,"k":"person","c":0.82,"x":0.11,"y":0.2,"w":0.09,"h":0.31,"l":"Truck","p":"A123BC"}]}]
  -- i=track_id, k=kind (DetectionKinds.ToDbString), c=confidence, x/y/w/h normalized 0..1 from the
  -- top-left corner (same convention as LiveDetection), l=label, p=plate_text (both optional).
  --
  -- jsonb rather than a normalized box table because there is never a predicate on geometry: the
  -- only access pattern is "give me everything in this wall-clock window", which the from_ts index
  -- already serves. A row per box would mean 30-75 inserts/s per camera and turn each cleanup tick
  -- into a delete of hundreds of thousands of rows.
  samples     jsonb       NOT NULL
);

-- Postgres does not index a foreign key automatically, and ClipCleaner deletes in batches of 100
-- every 5s — without this the cascade degrades linearly with the size of this table.
CREATE INDEX clip_metadata_chunks_clip_idx
  ON clip_metadata_chunks (clip_uid);

CREATE INDEX clip_metadata_chunks_lookup_idx
  ON clip_metadata_chunks (camera_id, stream_name, from_ts);

-- A clip row is keyed by file_path, so a path written a second time (an edge import over a live
-- segment, a re-run of a job) reuses the same clip_uid — and the cascade never fires. Geometry
-- recorded against the previous file describes a picture that is no longer there, exactly the
-- argument that already makes IngestAsync drop frag_index on a rewrite.
--
-- Purging by window rather than by "was the file rewritten", and that distinction was learned the
-- expensive way. Deleting a clip's geometry whenever start_ts changed threw away almost every box
-- ever recorded: a clip is ingested twice, and the two ingests legitimately disagree about
-- start_ts. open_new_segment() reports the moment the file was opened, while the writer is still
-- waiting for a keyframe; the first packet actually written then re-stamps m_segment_start_ts to
-- its own wall clock (segment_writer.cpp), and close_segment() reports that corrected value. So
-- start_ts moves on every ordinary segment close, and a rewrite test fires there.
--
-- The durable question is not "was the file rewritten" but "does this chunk still describe footage
-- this clip holds". An ordinary close nudges start_ts by the keyframe wait and drops nothing that
-- is inside; a genuine rewrite moves the window somewhere else entirely and takes all of the old
-- geometry with it. The margin matches MetadataSampleStore.BoundaryGrace, which is what allows a
-- frame to attach just outside a clip in the first place.
CREATE FUNCTION clip_metadata_purge_on_rewrite() RETURNS trigger AS $$
BEGIN
  IF NEW.start_ts IS DISTINCT FROM OLD.start_ts OR NEW.end_ts IS DISTINCT FROM OLD.end_ts THEN
    DELETE FROM clip_metadata_chunks
    WHERE clip_uid = OLD.clip_uid
      AND (to_ts < NEW.start_ts - interval '1500 milliseconds'
           OR (NEW.end_ts IS NOT NULL AND from_ts > NEW.end_ts + interval '1500 milliseconds'));
  END IF;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER clips_metadata_purge_on_rewrite
  BEFORE UPDATE ON clips
  FOR EACH ROW
  EXECUTE FUNCTION clip_metadata_purge_on_rewrite();
