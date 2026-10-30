-- Rights are rows on the map's tree now (VmsMap, migration 0003 there). The camera ACL was the
-- second rights system, and keeping it here would leave two answers to one question.
DROP TABLE IF EXISTS user_camera_permission;
DROP TABLE IF EXISTS user_all_cameras_permission;
DROP TABLE IF EXISTS camera_acl_setting;
