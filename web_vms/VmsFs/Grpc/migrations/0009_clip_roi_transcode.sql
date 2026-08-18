-- Re-encoding a finished clip with ROI happens at most once, and the clip is the
-- natural place to record that: the state dies with the clip, and the candidate
-- query reads all three columns, so none of them is speculative.
--
-- 'pending'  nothing has been tried yet
-- 'done'     re-encoded and the file replaced
-- 'skipped'  no detection boxes for this clip, so there is nothing to protect
-- 'failed'   attempts exhausted; left alone rather than retried forever
--
-- Why once and not repeatedly: this is lossy-to-lossy. Quality inside the zone
-- is preserved relative to the already-compressed clip, not to what the camera
-- saw, so a second pass would quietly cost another generation.
ALTER TABLE clips ADD COLUMN IF NOT EXISTS roi_state text NOT NULL DEFAULT 'pending';
ALTER TABLE clips ADD COLUMN IF NOT EXISTS roi_attempts integer NOT NULL DEFAULT 0;
ALTER TABLE clips ADD COLUMN IF NOT EXISTS roi_next_attempt_ts timestamptz NULL;

-- Partial: only pending rows are ever scanned, and they are the minority once a
-- deployment has been running for a while.
CREATE INDEX IF NOT EXISTS clips_roi_pending_idx
  ON clips (camera_id, end_ts)
  WHERE roi_state = 'pending';
