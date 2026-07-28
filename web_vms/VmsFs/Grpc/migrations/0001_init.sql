CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TABLE clips (
  clip_uid    uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
  file_path   text        NOT NULL UNIQUE,
  camera_id   text        NOT NULL,
  stream_name text        NOT NULL DEFAULT '',
  storage_id  text        NOT NULL DEFAULT '',
  start_ts    timestamptz NOT NULL,
  end_ts      timestamptz NULL,
  size_bytes  bigint      NOT NULL DEFAULT 0,
  retained    boolean     NOT NULL DEFAULT TRUE,
  -- Cached fmp4 fragment index (JSON), parsed once from the file and reused for HLS manifest
  -- builds so playback never re-reads the file to enumerate segments. Null until first parsed.
  frag_index  text        NULL
);

CREATE INDEX clips_camera_stream_time_idx
  ON clips (camera_id, stream_name, start_ts, end_ts);

CREATE INDEX idx_clips_camera_stream_start_retained
  ON clips (camera_id, stream_name, start_ts DESC) WHERE retained = TRUE;

CREATE INDEX idx_clips_retained_cleanup
  ON clips (retained, start_ts ASC);

-- Two-level event identity: event_uuid = one episode (a single open->close retention cycle, the row
-- a user selects); event_id = the group label (a time-clustered session of one object, possibly
-- spanning cameras). object_id is the re-identification label of the physical object.
CREATE TABLE retention_events (
  event_uuid     uuid        PRIMARY KEY DEFAULT gen_random_uuid(),
  event_id       text        NOT NULL,
  camera_id      text        NOT NULL,
  stream_name    text        NULL,
  alarm_start_ts timestamptz NOT NULL,
  alarm_end_ts   timestamptz NOT NULL,
  object_id      text        NULL
);

CREATE INDEX idx_retention_events_camera_time
  ON retention_events (camera_id, alarm_start_ts DESC, alarm_end_ts);

-- Supports the "latest episode for (event_id, camera_id)" lookup in RetainEvent and grouped listing.
CREATE INDEX idx_retention_events_group
  ON retention_events (event_id, camera_id, alarm_start_ts DESC);
