-- Per-operator slot preferences. Today just which stream a slot is pinned to, where the empty
-- string means "choose automatically from the tile's size" — the default, and the only state in
-- which the monitor is allowed to change stream by itself.
--
-- Per owner rather than per monitor, because a shared wall is watched by several people at once and
-- one of them pinning the main stream to look at a detail must not change what everyone else sees.
--
-- owner is a Keycloak preferred_username and is deliberately allowed to be empty, exactly as in the
-- monitor table above: KEYCLOAK_URL is optional, and with authentication disabled there is no
-- identity at all, so everything lands in the shared '' bucket.
CREATE TABLE monitor_slot_pref (
  owner       text NOT NULL,
  monitor_id  uuid NOT NULL REFERENCES monitor(id) ON DELETE CASCADE,
  slot_index  int  NOT NULL,
  stream_name text NOT NULL DEFAULT '',
  PRIMARY KEY (owner, monitor_id, slot_index)
);

CREATE INDEX idx_monitor_slot_pref_lookup
  ON monitor_slot_pref (owner, monitor_id);
