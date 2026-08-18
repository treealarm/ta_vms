-- What fraction of the clip's duration has detection boxes on it.
--
-- Recorded for every clip the ROI pass looks at, including the ones it decides
-- to skip: the thresholds that decide are guesses until there is a distribution
-- from real cameras to read them off, and this column is how that distribution
-- gets collected. Null means the pass has not looked yet.
ALTER TABLE clips ADD COLUMN IF NOT EXISTS roi_coverage real NULL;
