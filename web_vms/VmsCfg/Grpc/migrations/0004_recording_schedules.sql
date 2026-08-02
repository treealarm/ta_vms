-- Recording schedules were added by editing 0001_init.sql, which is a no-op for databases where
-- that file had already been recorded as applied: the migrator keys on the file name. Fresh
-- installs got the tables, existing ones never did, and VmsCfg then crashed on startup because
-- ScheduleCache.ReloadAsync queries them unconditionally.
--
-- IF NOT EXISTS throughout, so this is a no-op on databases created from the current 0001_init.sql.

CREATE TABLE IF NOT EXISTS recording_schedule (
  id              uuid    PRIMARY KEY DEFAULT gen_random_uuid(),
  name            text    NOT NULL UNIQUE,
  timezone        text    NOT NULL DEFAULT 'UTC',
  -- Seconds to switch recording on ahead of a window start, absorbing the keyframe wait and the
  -- notification round-trip so recording is already running when the window actually begins.
  lead_in_seconds integer NOT NULL DEFAULT 5
);

-- Seconds are counted from local midnight in the schedule's timezone, so 0..86400. end_second is
-- exclusive, and end_second <= start_second means the window wraps past midnight. Windows are
-- additive: a camera is recording if any window of any assigned schedule is open.
CREATE TABLE IF NOT EXISTS recording_schedule_window (
  id           uuid     PRIMARY KEY DEFAULT gen_random_uuid(),
  schedule_id  uuid     NOT NULL REFERENCES recording_schedule(id) ON DELETE CASCADE,
  kind         text     NOT NULL,  -- 'weekly' | 'date'
  day_of_week  smallint NULL,      -- 0=Sunday..6=Saturday, NULL for 'date'
  date_value   date     NULL,      -- NULL for 'weekly'
  start_second integer  NOT NULL,
  end_second   integer  NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_schedule_window_schedule ON recording_schedule_window (schedule_id);

CREATE TABLE IF NOT EXISTS camera_schedule (
  camera_id   text NOT NULL REFERENCES camera_setup(camera_id)  ON DELETE CASCADE,
  schedule_id uuid NOT NULL REFERENCES recording_schedule(id)   ON DELETE CASCADE,
  PRIMARY KEY (camera_id, schedule_id)
);
