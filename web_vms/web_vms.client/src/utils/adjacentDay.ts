// Day-to-day navigation over archive coverage.
//
// "Previous day" means the previous day that actually HAS recording, not yesterday's date: archive
// is full of holes (a camera offline for a week, retention already swept a month), and stepping one
// calendar day at a time would make the operator click through empty days one by one.

import { dayKeyLocal } from './wallTime'
import type { Segment } from './archiveRanges'

export type DayCoverage = ReadonlySet<string>

/**
 * Nearest day key strictly before (`dir = -1`) or after (`dir = 1`) `fromKey` that has coverage.
 * Null when there is none in that direction.
 */
export function adjacentCoveredDay(days: DayCoverage, fromKey: string, dir: -1 | 1): string | null {
  let best: string | null = null

  for (const key of days) {
    if (dir < 0) {
      if (key >= fromKey) continue
      if (best === null || key > best) best = key
    } else {
      if (key <= fromKey) continue
      if (best === null || key < best) best = key
    }
  }

  return best
}

/**
 * Local day keys touched by the given coverage. Note the server reports interval boundaries in UTC
 * while navigation is local, so days are derived here rather than taken from the UTC-bucketed
 * recording-coverage endpoint.
 */
export function coveredDayKeys(segments: readonly Segment[]): Set<string> {
  const keys = new Set<string>()

  for (const seg of segments) {
    // Walk local midnights so a segment spanning a day boundary marks both days.
    let cursor = new Date(seg.startMs)
    cursor.setHours(0, 0, 0, 0)

    while (cursor.getTime() < seg.endMs) {
      keys.add(dayKeyLocal(cursor.getTime()))
      const next = new Date(cursor)
      next.setDate(next.getDate() + 1)
      cursor = next
    }
  }

  return keys
}
