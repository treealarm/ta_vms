ALTER TABLE onvif_event_subscription
  ADD COLUMN poll_timeout_seconds integer NOT NULL DEFAULT 5,
  ADD COLUMN message_limit        integer NOT NULL DEFAULT 1024;
