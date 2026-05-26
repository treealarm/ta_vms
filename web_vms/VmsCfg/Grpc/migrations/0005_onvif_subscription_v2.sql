ALTER TABLE onvif_event_subscription
  ADD COLUMN IF NOT EXISTS subscribed boolean NOT NULL DEFAULT true,
  DROP COLUMN IF EXISTS created_at;

ALTER TABLE onvif_alarm_filter
  DROP COLUMN IF EXISTS created_at;
