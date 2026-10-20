-- Which model produced each embedding (analytics.proto DetectionEvent.embedding_model). Vectors of
-- different models are not comparable, even when they are the same length, so search and the
-- re-id gallery compare a vector only with vectors of its own model. NULL marks the rows written
-- before the worker named its model -- all of them came from the OSNet 512-d body model it shipped
-- until then -- and the vectors of a worker that still does not.
ALTER TABLE detection_embeddings ADD COLUMN model text NULL;

CREATE INDEX idx_detection_embeddings_kind_model ON detection_embeddings (kind, model);
