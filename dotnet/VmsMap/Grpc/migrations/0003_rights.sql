-- Rights became rows on tree nodes in 0001; what was missing was the switch that says what a node
-- with no rows means. It lived in VmsCfg's camera_acl_setting until M3, next to an ACL that was a
-- second rights system. It belongs to the tree now, so it moves here.
--
-- A fresh installation starts enforced, exactly as the camera ACL did: nobody has been granted
-- anything yet, and a new deployment must not open every camera by default.
CREATE TABLE rights_mode (
  id   boolean PRIMARY KEY DEFAULT true CHECK (id),
  mode text NOT NULL
);

INSERT INTO rights_mode (mode) VALUES ('enforced');
