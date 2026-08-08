-- Outages are only recorded when recording was supposed to be running.
--
-- The flag turned out to have exactly one useful value. An outage while recording is off — no
-- schedule window, no arming, no alarm — costs no footage: there is nothing to restore from the
-- camera's card because nothing was going to be written anyway. Those rows were the bulk of the
-- table and answered a question nobody asks, so they are not stored at all rather than stored and
-- filtered on read.
DELETE FROM source_outages WHERE NOT recording_expected;

-- The scheduler's index no longer needs the flag: every remaining row qualifies.
DROP INDEX IF EXISTS idx_source_outages_pending;
CREATE INDEX idx_source_outages_pending
  ON source_outages (state, next_retry_ts) WHERE state = 'open';

ALTER TABLE source_outages DROP COLUMN recording_expected;
