-- Where a clip's footage came from.
--
-- A range pulled back off the camera's own storage is not a continuation of the live recording: it
-- was written by a different device, arrives hours later, and is allowed to overlap its neighbours
-- by a second or two because losing frames at the seam is worse than a small overlap. Merging it
-- into the same stretch of archive as the live clips hides all of that from the operator, so the
-- provenance is stored and the two are kept apart when ranges are assembled.
ALTER TABLE clips ADD COLUMN IF NOT EXISTS origin text NOT NULL DEFAULT 'live';

-- Imports written before this column existed are recognisable by the marker in their file name,
-- which the importer has always added (without it avio_open would overwrite a live clip).
UPDATE clips SET origin = 'edge' WHERE file_path LIKE '%\_edge.mp4';
