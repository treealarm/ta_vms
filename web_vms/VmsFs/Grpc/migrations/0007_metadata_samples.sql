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

-- A clip row is keyed by file_path, so a path that gets written a second time (an edge import over
-- a live segment, a re-run of a job) reuses the same clip_uid — and the cascade never fires. The
-- geometry recorded against the previous file describes a picture that is no longer there, exactly
-- the argument that already makes IngestAsync drop frag_index on a rewrite.
--
-- The discriminator is start_ts, NOT "the ingest carried an end_ts": every ordinary segment close
-- carries one, and keying off that would wipe the geometry of every clip the moment it finalized.
-- A changed start_ts is what actually means "different recording behind the same path".
CREATE FUNCTION clip_metadata_purge_on_rewrite() RETURNS trigger AS $$
BEGIN
  IF NEW.start_ts IS DISTINCT FROM OLD.start_ts THEN
    DELETE FROM clip_metadata_chunks WHERE clip_uid = OLD.clip_uid;
  END IF;
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER clips_metadata_purge_on_rewrite
  BEFORE UPDATE ON clips
  FOR EACH ROW
  EXECUTE FUNCTION clip_metadata_purge_on_rewrite();
