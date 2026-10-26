-- The deployment's nodes: one row per media server with the services pinned to its machine, which
-- MEDIA_SERVER_PAIRS alone used to name, so that a node joins or leaves without the core restarting.
-- Rows of MEDIA_SERVER_PAIRS are written at every start and marked configured; a node enrolled over
-- the network (EnrollNode) is not, and may be removed.
CREATE TABLE media_server_node (
  server_id   text        PRIMARY KEY,
  fs_id       text        NOT NULL,
  onvif_id    text        NOT NULL,
  logger_id   text        NOT NULL,
  roi_id      text        NOT NULL DEFAULT '',
  name        text        NOT NULL UNIQUE,
  address     text        NOT NULL DEFAULT '',
  media_mtx   boolean     NOT NULL DEFAULT false,
  configured  boolean     NOT NULL DEFAULT false,
  enrolled_at timestamptz NOT NULL DEFAULT now()
);

-- Whether a machine may enroll without an administrator's password: open while the deployment is
-- being set up, closed by the administrator once every machine is in. One row.
CREATE TABLE deployment (
  id              int     PRIMARY KEY CHECK (id = 1),
  enrollment_open boolean NOT NULL DEFAULT true
);
INSERT INTO deployment (id) VALUES (1);
