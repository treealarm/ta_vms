-- Replaces the purge rule introduced with clip_metadata_chunks in 0007.
--
-- 0007 deleted a clip's geometry whenever start_ts changed, on the theory that a moved start means
-- the file was rewritten. That is wrong, and it threw away almost every box ever recorded: a clip
-- is ingested twice, and the two ingests legitimately disagree about start_ts. open_new_segment()
-- reports the moment the file was opened, while the writer is still waiting for a keyframe; the
-- first packet actually written then re-stamps m_segment_start_ts to its own wall clock
-- (segment_writer.cpp), and close_segment() reports that corrected value. So start_ts moves on
-- every ordinary segment close, and the trigger fired there rather than on a rewrite.
--
-- The durable question is not "was the file rewritten" but "does this chunk still describe footage
-- this clip holds". Purging by window answers it directly and needs no rewrite detection: an
-- ordinary close nudges start_ts by the keyframe wait and drops nothing that is inside, while a
-- genuine rewrite moves the window somewhere else entirely and takes all of the old geometry with
-- it. The margin matches MetadataSampleStore.BoundaryGrace, which is what allows a frame to attach
-- just outside a clip in the first place.
CREATE OR REPLACE FUNCTION clip_metadata_purge_on_rewrite() RETURNS trigger AS $$
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
