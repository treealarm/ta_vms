-- Per-watch retention timeout, replacing the service-wide ANALYTICS_RETAIN_TIMEOUT_SECONDS.
-- Default matches onvif_alarm_filter.timeout_seconds so both event sources behave alike.
ALTER TABLE analytics_watch
  ADD COLUMN timeout_seconds integer NOT NULL DEFAULT 10;
