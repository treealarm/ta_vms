CREATE TABLE IF NOT EXISTS camera_setup (
  camera_id text PRIMARY KEY,
  rtsp_url text NOT NULL DEFAULT '',
  cred_user text NULL,
  cred_pass text NULL,
  onvif_ip text NULL,
  onvif_port integer NULL
);

CREATE TABLE IF NOT EXISTS camera_state (
  camera_id text PRIMARY KEY REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  recording boolean NOT NULL DEFAULT false,
  web_rtc boolean NOT NULL DEFAULT false
);
