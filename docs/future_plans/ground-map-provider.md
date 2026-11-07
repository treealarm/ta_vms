# A ground map provider of our own

**Found** while building M2.9 (`docs/plans/square-merge/m2.9-real-campus-world.md`), 2026-11-07.

OnvifSim can draw a raster map under a world (`terrain.map`). The picture is fetched once per
installation from whatever `ONVIFSIM_MAP_TILE_URL` names; the default is the standard OpenStreetMap
tile service, whose usage policy forbids bulk downloading and offline maps. We do not redistribute
pixels — each installation fetches a few dozen tiles for its own world — but a product deployment
that wants to ship a map, or to work offline, cannot lean on that service.

**To do:** pick one of
- a self-hosted tile server (e.g. a vector-tile pipeline over an OSM extract) run beside the product;
- a commercial provider whose licence allows caching and redistribution;
- our own renderer of the OSM *data* (the vector import in `OsmService` already reads it) into the
  ground's vertex colours or a picture — the most work, and the only one that removes the dependency
  entirely.

The attribution line (`terrain.mapAttribution`) has to stay either way, and a provider change must
re-bake every cached map.
