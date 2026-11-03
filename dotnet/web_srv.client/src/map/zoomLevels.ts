import type { MapFigure, MapLevel } from '../dto/mapDtos'

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

/**
 * The zoom a container is entered at: the lowest minimum among the layers its children are drawn
 * from. Undefined when no child carries a layer — or there are no children at all — and the caller
 * steps in by one instead.
 */
export function enterZoom(children: MapFigure[], levels: MapLevel[]): number | undefined {
  const ranges = levelRanges(levels)
  let lowest: number | undefined
  for (const child of children) {
    const range = child.zoomLevel ? ranges.get(child.zoomLevel) : undefined
    if (range) lowest = lowest === undefined ? range.min : Math.min(lowest, range.min)
  }
  return lowest
}
