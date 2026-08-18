-- How long a clip stays untouched before the ROI pass may re-encode it.
--
-- Hours rather than seconds because that is the scale operators think in, and
-- fractional because the useful values are as often minutes. 0 does not mean
-- "immediately": the pass still waits for the clip to close and for the boxes to
-- be written, which is a correctness floor and lives in code, not here.
ALTER TABLE camera_setup ADD COLUMN IF NOT EXISTS roi_delay_hours double precision NOT NULL DEFAULT 0;
