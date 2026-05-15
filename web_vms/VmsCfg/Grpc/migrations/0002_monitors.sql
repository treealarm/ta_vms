CREATE TABLE monitor (
  id      uuid  PRIMARY KEY DEFAULT gen_random_uuid(),
  name    text  NOT NULL UNIQUE,
  layout  text  NOT NULL DEFAULT '2x2'
);

-- Ordered pool of cameras assigned to a monitor (more cameras can exist than layout slots)
CREATE TABLE monitor_camera (
  monitor_id  uuid  NOT NULL REFERENCES monitor(id) ON DELETE CASCADE,
  position    int   NOT NULL,
  camera_id   text  NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  PRIMARY KEY (monitor_id, position)
);

CREATE INDEX idx_monitor_camera_mid ON monitor_camera (monitor_id, position ASC);

-- Which camera is currently active in each layout slot (persisted)
CREATE TABLE monitor_slot (
  monitor_id  uuid  NOT NULL REFERENCES monitor(id) ON DELETE CASCADE,
  slot_index  int   NOT NULL,
  camera_id   text  NOT NULL REFERENCES camera_setup(camera_id) ON DELETE CASCADE,
  PRIMARY KEY (monitor_id, slot_index)
);
