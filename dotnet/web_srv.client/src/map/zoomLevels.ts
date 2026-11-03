import type { MapLevel } from '../dto/mapDtos'

/** The zoom range a scale layer covers. */
export type LevelRange = { min: number; max: number }

/** The range of every scale layer, by name — what the visibility test needs. */
export function levelRanges(levels: MapLevel[]): Map<string, LevelRange> {
  const ranges = new Map<string, LevelRange>()
  for (const level of levels) {
    if (level.zoomLevel) {
      ranges.set(level.zoomLevel, { min: level.zoomMin ?? 0, max: level.zoomMax ?? 99 })
    }
  }
  return ranges
}

/**
 * Whether a node on a scale layer is drawn at this zoom. A node without a layer — and a layer the
 * dictionary no longer lists — is always drawn: removing the levels shows everything at once.
 */
export function inLayer(
  ranges: Map<string, LevelRange>, zoomLevel: string | null | undefined, zoom: number,
): boolean {
  if (!zoomLevel) return true
  const range = ranges.get(zoomLevel)
  return !range || (zoom >= range.min && zoom <= range.max)
}
