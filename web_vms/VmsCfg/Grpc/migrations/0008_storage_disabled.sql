-- A storage that is out of service without being removed.
--
-- Deleting the row would take its camera assignments with it, so a disk pulled for maintenance
-- and put back would mean reassigning every camera by hand. Disabled keeps the row and the
-- assignments; it only stops new recordings from choosing it. Clips already on it stay readable,
-- because playback, export and cleanup all resolve their paths through the same row.
ALTER TABLE storage ADD COLUMN IF NOT EXISTS disabled boolean NOT NULL DEFAULT false;
