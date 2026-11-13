-- Arming a zone is the map's own state, not one of an object's free-form properties: a row here
-- means the node is set to armed or disarmed explicitly, and no row means it follows its parent.
CREATE TABLE object_arming (
  object_id uuid    PRIMARY KEY REFERENCES objects (id) ON DELETE CASCADE,
  armed     boolean NOT NULL
);

-- A row in `properties` was the only way to say "explicitly armed/disarmed": carry those over,
-- keeping both answers, and take the property away so one node has one place to say it.
INSERT INTO object_arming (object_id, armed)
SELECT object_id, str_val = 'true'
FROM properties
WHERE prop_name = 'armed' AND str_val IN ('true', 'false');

DELETE FROM properties WHERE prop_name = 'armed';
