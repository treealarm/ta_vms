-- Per-user camera access. username is a Keycloak preferred_username — the same identity string
-- monitor.owner already stores. There is deliberately no foreign key to a user table: Keycloak owns
-- identities, VmsCfg only labels grants with them, and a row naming a user Keycloak no longer has
-- is inert.
CREATE TABLE user_camera_permission (
  username     text    NOT NULL,
  camera_id    text    NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  view_live    boolean NOT NULL DEFAULT false,
  view_archive boolean NOT NULL DEFAULT false,
  ptz          boolean NOT NULL DEFAULT false,
  control      boolean NOT NULL DEFAULT false,
  export       boolean NOT NULL DEFAULT false,
  PRIMARY KEY (username, camera_id)
);

CREATE INDEX idx_user_camera_permission_username ON user_camera_permission (username);

-- Blanket grant covering every camera, including ones added later. The effective permission on a
-- camera is the OR of this row and the matching user_camera_permission row, so "live on everything
-- plus PTZ on the gate camera" needs no per-camera copies of the blanket flags.
CREATE TABLE user_all_cameras_permission (
  username     text    PRIMARY KEY,
  view_live    boolean NOT NULL DEFAULT false,
  view_archive boolean NOT NULL DEFAULT false,
  ptz          boolean NOT NULL DEFAULT false,
  control      boolean NOT NULL DEFAULT false,
  export       boolean NOT NULL DEFAULT false
);

-- Single-row switch for the whole ACL. 'open' = every authenticated user sees every camera (the
-- behaviour before this migration existed), 'enforced' = the tables above decide.
CREATE TABLE camera_acl_setting (
  id   boolean PRIMARY KEY DEFAULT true CHECK (id),
  mode text    NOT NULL
);

-- An upgrade of a running system must not black out its operators, so an install that already has
-- cameras starts 'open' and the admin flips the switch once grants are in place. A fresh install
-- has nobody to lock out, so it starts secure.
INSERT INTO camera_acl_setting (mode)
SELECT CASE WHEN EXISTS (SELECT 1 FROM camera_setup) THEN 'open' ELSE 'enforced' END;
