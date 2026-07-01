CREATE TABLE analytics_watch (
  id              uuid    PRIMARY KEY DEFAULT gen_random_uuid(),
  rtsp_url        text    NOT NULL,
  cred_user       text    NULL,
  cred_pass       text    NULL,
  enabled         boolean NOT NULL DEFAULT false,
  classes         text    NOT NULL DEFAULT 'person',
  min_confidence  real    NOT NULL DEFAULT 0.5,
  sample_fps      integer NOT NULL DEFAULT 1
);

CREATE TABLE analytics_watch_camera (
  watch_id   uuid NOT NULL REFERENCES analytics_watch(id) ON DELETE CASCADE,
  camera_id  text NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  PRIMARY KEY (watch_id, camera_id)
);
