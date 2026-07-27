-- Two-level retention identity. Historically `event_id` was the PRIMARY KEY and doubled as both the
-- unique row and the extension key. Split it: `event_uuid` becomes the per-episode primary key
-- (one retention open->close cycle), `event_id` becomes a non-unique GROUP label (a time-clustered
-- session of one object, possibly spanning cameras). `object_id` records the re-identification label.
--
-- Backfill is deterministic (md5(event_id)::uuid) so the migration is idempotent and existing rows
-- become singleton groups (each old event_id -> one episode). The whole file runs in one transaction
-- under the migrator's advisory lock, so the PK swap is atomic.
ALTER TABLE retention_events ADD COLUMN event_uuid uuid;
UPDATE retention_events SET event_uuid = md5(event_id)::uuid WHERE event_uuid IS NULL;
ALTER TABLE retention_events ALTER COLUMN event_uuid SET NOT NULL;

ALTER TABLE retention_events DROP CONSTRAINT retention_events_pkey;
ALTER TABLE retention_events ADD PRIMARY KEY (event_uuid);

ALTER TABLE retention_events ADD COLUMN object_id text NULL;

-- Supports the "latest episode for (event_id, camera_id)" lookup in RetainEvent and grouped listing.
CREATE INDEX idx_retention_events_group
  ON retention_events (event_id, camera_id, alarm_start_ts DESC);
