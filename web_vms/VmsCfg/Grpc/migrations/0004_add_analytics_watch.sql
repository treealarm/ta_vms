CREATE TABLE analytics_watch (
  camera_id          text    PRIMARY KEY REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  stream_name        text    NOT NULL,
  enabled            boolean NOT NULL DEFAULT false,
  classes            text    NOT NULL DEFAULT 'person',
  min_confidence     real    NOT NULL DEFAULT 0.5,
  sample_fps         integer NOT NULL DEFAULT 1,
  attach_debug_crops boolean NOT NULL DEFAULT false
);
