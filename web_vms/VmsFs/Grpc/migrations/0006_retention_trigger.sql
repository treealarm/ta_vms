-- What set the alarm off, in the producer's own words: the ONVIF topic for an ONVIF alarm
-- ("tns1:RuleEngine/CellMotionDetector/Motion"), NULL for anything that does not name a cause.
-- Without it an episode records only that something happened on a camera, which cannot distinguish
-- motion from tamper when several alarm filters are armed at the same time.
ALTER TABLE retention_events ADD COLUMN trigger text NULL;
