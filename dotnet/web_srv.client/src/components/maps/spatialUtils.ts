import type { CameraPlacementDto, SpaceDto } from '../../dto/dtos'

const byOrder = (a: SpaceDto, b: SpaceDto) => a.ordinal - b.ordinal || a.name.localeCompare(b.name)

/** Spaces with no parent, in order. */
export const rootsOf = (spaces: SpaceDto[]): SpaceDto[] => spaces.filter((s) => !s.parentId).sort(byOrder)

/** The spaces directly inside one, in order: floors bottom to top. */
export const childrenOf = (spaces: SpaceDto[], parentId: string): SpaceDto[] =>
  spaces.filter((s) => s.parentId === parentId).sort(byOrder)

/** Spaces sharing a parent with this one, itself included — the floors of one building. */
export const siblingsOf = (spaces: SpaceDto[], space: SpaceDto): SpaceDto[] =>
  space.parentId ? childrenOf(spaces, space.parentId) : rootsOf(spaces)

/** From the root down to the space itself — the breadcrumbs. */
export function pathTo(spaces: SpaceDto[], spaceId: string): SpaceDto[] {
  const byId = new Map(spaces.map((s) => [s.id, s]))
  const path: SpaceDto[] = []
  // Bounded by the number of spaces, so a loop in the data cannot hang the page.
  for (let at = byId.get(spaceId); at && path.length <= spaces.length; at = at.parentId ? byId.get(at.parentId) : undefined) {
    path.unshift(at)
  }
  return path
}

/** The space and everything nested in it, depth first, each with its depth — the admin's tree. */
export function treeOf(spaces: SpaceDto[]): { space: SpaceDto; depth: number }[] {
  const out: { space: SpaceDto; depth: number }[] = []
  const walk = (list: SpaceDto[], depth: number) => {
    for (const s of list) {
      out.push({ space: s, depth })
      if (s.id && depth < spaces.length) walk(childrenOf(spaces, s.id), depth + 1)
    }
  }
  walk(rootsOf(spaces), 0)
  return out
}

/**
 * Whether two spaces share geometry: the same space, or both in real coordinates. A camera can be
 * aimed at, or calibrated on, a point only in a space that shares its own's. VmsCfg and the
 * tracking service apply the same rule.
 */
export const sharesGeometry = (a: SpaceDto | undefined, b: SpaceDto | undefined): boolean =>
  !!a && !!b && (a.id === b.id || (a.realCoordinates && b.realCoordinates))

/**
 * The spaces to mark for a set of alarming cameras: every space one of them is drawn in — where
 * it stands and its pictures alike — and every space those are nested in, so the outline of a
 * building on the site map burns when a camera on any of its floors does.
 */
export function alarmedSpaces(spaces: SpaceDto[], placements: CameraPlacementDto[], cameraIds: ReadonlySet<string>): Set<string> {
  const out = new Set<string>()
  for (const p of placements) {
    if (!cameraIds.has(p.cameraId) || out.has(p.spaceId)) continue
    for (const s of pathTo(spaces, p.spaceId)) if (s.id) out.add(s.id)
  }
  return out
}

/** "41.3111, 69.2797" → { lat, lon }; null when it does not read as a coordinate pair. */
export function parseLatLon(text: string): { lat: number; lon: number } | null {
  const parts = text.split(/[,;\s]+/).filter(Boolean).map((p) => Number(p))
  if (parts.length !== 2 || parts.some((p) => !Number.isFinite(p))) return null
  const [lat, lon] = parts
  if (lat < -90 || lat > 90 || lon < -180 || lon > 180) return null
  return { lat, lon }
}

export const formatLatLon = (lat: number, lon: number) => `${lat.toFixed(6)}, ${lon.toFixed(6)}`
