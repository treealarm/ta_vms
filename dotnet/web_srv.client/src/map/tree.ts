import type { MapMarker } from '../dto/mapDtos'

/**
 * Whether `id` sits anywhere below `ancestor` in the tree. Bounded by the number of known nodes, so
 * a loop already in the data cannot hang the page. A node cannot be moved under itself or under one
 * of its descendants — the server refuses that too.
 */
export function isUnder(
  markers: Record<string, MapMarker>, id: string, ancestor: string, limit = 1000,
): boolean {
  let at = markers[id]?.parentId
  for (let steps = 0; at && steps <= limit; steps++) {
    if (at === ancestor) return true
    at = markers[at]?.parentId
  }
  return false
}
