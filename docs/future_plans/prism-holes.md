# Prism footprints with holes

**Found** while building M2.9 (`docs/plans/square-merge/m2.9-real-campus-world.md`), 2026-11-07.

OnvifSim's `prism` object draws walls, a roof and a floor from one simple outline (see
`OnvifSim/src/OnvifSim.Server/Geometry/Polygon.cs`). Footprints that cross themselves and
multipolygons **with inner rings** are refused with a message instead of being repaired: the OSM
import marks such a candidate `hasHoles` and the editor refuses to build it.

That is honest but limiting: a building with a courtyard (A. James Clark Hall on the demo campus is
one) cannot be imported as a whole — only its parts, one at a time, if the parts exist as separate
ways.

**To do:** carry inner rings through `WorldObject.Points` (or a second list), triangulate rings with
holes (a hole-bridging ear clip, or a constrained Delaunay), and draw the walls of the inner rings
facing inwards. The validator gains "every hole is inside the outline and does not cross another
ring". Until then the refusal stays: a lid over a courtyard is worse than a message.
