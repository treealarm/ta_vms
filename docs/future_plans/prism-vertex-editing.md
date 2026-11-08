# Editing a prism's footprint in the editor

**Found** while building M2.9 (`docs/plans/square-merge/m2.9-real-campus-world.md`), 2026-11-07.

A `prism` object is created by drawing its outline point by point, or by importing it from
OpenStreetMap, and its card edits the height, the colours and the openings. The points themselves
are **not** draggable: to change the outline one deletes the object and draws it again.

**To do:** show the outline as handles in the viewport (the machinery for dragging a road's points
already exists — `movePoint` in `OnvifSim.Web/src/store.ts`) and write the moved ring back through
`normalizeOutline`. The catch is the openings: their edge index and offset are measured along the
stored ring, so moving or inserting a point has to remap them, which is the same problem as keeping
provenance of an imported OSM object while editing it.
