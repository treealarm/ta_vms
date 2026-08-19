-- The band of ROI coverage in which re-encoding a clip is worth doing: below the
-- floor the detector barely saw anything and crushing the clip takes its word for
-- the whole thing, above the ceiling there is little background left to crush.
--
-- Per camera, because the band is a property of the scene: what counts as "the
-- detector barely saw anything" on a corridor is an ordinary afternoon on a
-- checkout counter, and one pair of numbers cannot be right for both.
--
-- Defaults keep the band wide open, which is what the node-wide environment
-- variables these columns replace were set to. That is deliberate: it is also the
-- fastest way to fill clips.roi_coverage and find out what the real distribution
-- looks like, so the per-camera numbers can be measured rather than guessed.
ALTER TABLE camera_setup ADD COLUMN IF NOT EXISTS roi_min_coverage double precision NOT NULL DEFAULT 0;
ALTER TABLE camera_setup ADD COLUMN IF NOT EXISTS roi_max_coverage double precision NOT NULL DEFAULT 1;
