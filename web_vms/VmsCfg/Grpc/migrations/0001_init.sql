CREATE TABLE IF NOT EXISTS media_server_node (
  server_id text PRIMARY KEY,
  status text NOT NULL DEFAULT 'online',
  last_seen timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS camera_setup (
  camera_id text PRIMARY KEY,
  server_id text NOT NULL REFERENCES media_server_node(server_id),
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
