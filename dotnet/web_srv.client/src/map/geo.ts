import type { CameraPlacementDto, GeoBoundsDto, GeoPointDto, PlanCornersDto, SpaceDto } from '../dto/dtos'

// The browser's half of the spatial maths: just enough to draw view cones, place a plan image and
// show distances. Aiming is done on the server (VmsPtzTrackingSingleton/Geo), which owns the fit.

const EARTH_RADIUS_M = 6378137
const DEG = Math.PI / 180

/** Metres east and north of an origin. Equirectangular: exact enough for a site of a few hundred metres. */
export function toEnu(origin: GeoPointDto, p: GeoPointDto): { east: number; north: number } {
  return {
    east: (p.lon - origin.lon) * DEG * EARTH_RADIUS_M * Math.cos(origin.lat * DEG),
    north: (p.lat - origin.lat) * DEG * EARTH_RADIUS_M,
  }
}

export function fromEnu(origin: GeoPointDto, east: number, north: number): GeoPointDto {
  return {
    lat: origin.lat + north / (DEG * EARTH_RADIUS_M),
    lon: origin.lon + east / (DEG * EARTH_RADIUS_M * Math.cos(origin.lat * DEG)),
  }
}

/** The point `metres` away along a compass bearing (0 = north, clockwise). */
export function destination(from: GeoPointDto, bearingDeg: number, metres: number): GeoPointDto {
  return fromEnu(from, metres * Math.sin(bearingDeg * DEG), metres * Math.cos(bearingDeg * DEG))
}

/** Compass bearing from one point to another, in [0, 360). */
export function bearing(from: GeoPointDto, to: GeoPointDto): number {
  const { east, north } = toEnu(from, to)
  const deg = Math.atan2(east, north) / DEG
  return deg < 0 ? deg + 360 : deg
}

export function distance(from: GeoPointDto, to: GeoPointDto): number {
  const { east, north } = toEnu(from, to)
  return Math.hypot(east, north)
}

/** A camera's view cone as a polygon: apex at the camera, arc at its range. */
export function fovPolygon(p: CameraPlacementDto): [number, number][] {
  const apex = { lat: p.lat, lon: p.lon }
  const half = Math.min(p.hfovDeg, 359) / 2
  const steps = Math.max(4, Math.ceil(p.hfovDeg / 10))
  const points: [number, number][] = [[apex.lat, apex.lon]]
  for (let i = 0; i <= steps; i++) {
    const b = p.headingDeg - half + (2 * half * i) / steps
    const q = destination(apex, b, p.rangeM)
    points.push([q.lat, q.lon])
  }
  return points
}

/**
 * Where an image lies on the ground, given three of its corners: pixel (x, y) of a w×h image maps
 * to topLeft + x/w·(topRight − topLeft) + y/h·(bottomLeft − topLeft). An affine map, so it holds
 * rotation and scale, and the inverse is a 2×2 solve.
 */
export function planAffine(c: PlanCornersDto, widthPx: number, heightPx: number) {
  const ax = (c.topRight.lon - c.topLeft.lon) / widthPx
  const ay = (c.topRight.lat - c.topLeft.lat) / widthPx
  const bx = (c.bottomLeft.lon - c.topLeft.lon) / heightPx
  const by = (c.bottomLeft.lat - c.topLeft.lat) / heightPx
  const det = ax * by - bx * ay
  return {
    pixelToGeo: (x: number, y: number): GeoPointDto => ({
      lon: c.topLeft.lon + x * ax + y * bx,
      lat: c.topLeft.lat + x * ay + y * by,
    }),
    geoToPixel: (p: GeoPointDto): { x: number; y: number } => {
      const dLon = p.lon - c.topLeft.lon
      const dLat = p.lat - c.topLeft.lat
      return { x: (dLon * by - dLat * bx) / det, y: (ax * dLat - ay * dLon) / det }
    },
  }
}

/**
 * Corners for an image laid flat, north-up, centred on a point, `widthM` metres across — the
 * starting placement of a freshly uploaded plan, before anyone drags its corners.
 */
export function cornersAround(centre: GeoPointDto, widthPx: number, heightPx: number, widthM: number): PlanCornersDto {
  const heightM = (widthM * heightPx) / widthPx
  return {
    topLeft: fromEnu(centre, -widthM / 2, heightM / 2),
    topRight: fromEnu(centre, widthM / 2, heightM / 2),
    bottomLeft: fromEnu(centre, -widthM / 2, -heightM / 2),
  }
}

/** The fourth corner of the parallelogram, for drawing outlines and handles. */
export function bottomRight(c: PlanCornersDto): GeoPointDto {
  return {
    lat: c.topRight.lat + c.bottomLeft.lat - c.topLeft.lat,
    lon: c.topRight.lon + c.bottomLeft.lon - c.topLeft.lon,
  }
}

/**
 * Rescales a placed plan so that the distance between two pixels of it equals `metres`, keeping its
 * centre and rotation — how a plan with no map underneath is given its true size.
 */
export function rescaleCorners(
  c: PlanCornersDto, widthPx: number, heightPx: number,
  a: { x: number; y: number }, b: { x: number; y: number }, metres: number,
): PlanCornersDto {
  const affine = planAffine(c, widthPx, heightPx)
  const current = distance(affine.pixelToGeo(a.x, a.y), affine.pixelToGeo(b.x, b.y))
  if (!(current > 0) || !(metres > 0)) return c
  const k = metres / current
  const centre = affine.pixelToGeo(widthPx / 2, heightPx / 2)
  const scale = (p: GeoPointDto) => {
    const e = toEnu(centre, p)
    return fromEnu(centre, e.east * k, e.north * k)
  }
  return { topLeft: scale(c.topLeft), topRight: scale(c.topRight), bottomLeft: scale(c.bottomLeft) }
}

/** A square view of the given half-size around a point — the default frame of a site with nothing else to go by. */
export function boundsAround(centre: GeoPointDto, halfSizeM: number): GeoBoundsDto {
  return {
    southWest: fromEnu(centre, -halfSizeM, -halfSizeM),
    northEast: fromEnu(centre, halfSizeM, halfSizeM),
  }
}

export const boundsCentre = (b: GeoBoundsDto): GeoPointDto => ({
  lat: (b.southWest.lat + b.northEast.lat) / 2,
  lon: (b.southWest.lon + b.northEast.lon) / 2,
})

/** A rectangle a fifth of the parent's view across, in the middle of it — where a new footprint starts. */
export function defaultFootprint(parent: SpaceDto): GeoPointDto[] {
  const { southWest: sw, northEast: ne } = parent.view
  const cLat = (sw.lat + ne.lat) / 2
  const cLon = (sw.lon + ne.lon) / 2
  const hLat = (ne.lat - sw.lat) / 10
  const hLon = (ne.lon - sw.lon) / 10
  return [
    { lat: cLat + hLat, lon: cLon - hLon },
    { lat: cLat + hLat, lon: cLon + hLon },
    { lat: cLat - hLat, lon: cLon + hLon },
    { lat: cLat - hLat, lon: cLon - hLon },
  ]
}
