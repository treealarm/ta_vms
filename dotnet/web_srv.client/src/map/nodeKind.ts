import type { MapMarker } from '../dto/mapDtos'

// The server returns a page in id order, which is a hash order to the reader. Within a page the
// tree is sorted the way a person looks for things: spaces first, then what sits in them.
export const KIND_ORDER: Record<string, number> = {
  space: 0,
  diagram: 1,
  camera: 2,
  part: 3,
  landmark: 4,
  figure: 5,
  object: 6,
}

export function sortMarkers(markers: MapMarker[]): MapMarker[] {
  return [...markers].sort((a, b) => {
    const byKind = (KIND_ORDER[a.nodeKind ?? 'object'] ?? 5) - (KIND_ORDER[b.nodeKind ?? 'object'] ?? 5)
    return byKind !== 0 ? byKind : (a.name || a.id).localeCompare(b.name || b.id)
  })
}
