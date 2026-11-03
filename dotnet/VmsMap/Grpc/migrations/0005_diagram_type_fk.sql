-- Deleting a type used to fail on the diagrams that still named it, although the dictionary's own
-- dialog promises those diagrams keep their place and lose only their picture. A type is a shared
-- description, not a parent: the node stays, drawn by nothing, until another type is chosen for it.
ALTER TABLE diagrams
  DROP CONSTRAINT IF EXISTS diagrams_dgr_type_fkey;

ALTER TABLE diagrams
  ADD CONSTRAINT diagrams_dgr_type_fkey
  FOREIGN KEY (dgr_type) REFERENCES diagram_types (id) ON DELETE SET NULL;
