// Tick layout for the wall timeline.
//
// Two rules drive everything here:
//  - a full date belongs only where the day actually changes. Stamping "2026-07-28 14:31" on every
//    tick turns the ruler into a wall of text that is harder to read than no labels at all;
//  - ticks are dense over recorded stretches, where the operator scrubs, and sparse over empty
//    ones, which are only being crossed.

import type { Segment } from './archiveRanges'
import { DAY_MS, HOUR_MS, MINUTE_MS, SECOND_MS, isSameLocalDay, localDayStartMs, dayKeyLocal } from './wallTime'
import type { WallRange } from './wallTime'

export type TickKind = 'major' | 'minor' | 'edge'

export type Tick = {
  ms: number
  label: string
  kind: TickKind
  /** True only where the label must carry the calendar date, not just the time. */
  showDate: boolean
}

export function msToX(ms: number, win: WallRange, pxWidth: number): number {
  const span = win.endMs - win.startMs
  if (span <= 0) return 0
  return ((ms - win.startMs) / span) * pxWidth
}

export function xToMs(x: number, win: WallRange, pxWidth: number): number {
  if (pxWidth <= 0) return win.startMs
  return win.startMs + (x / pxWidth) * (win.endMs - win.startMs)
}

/** Wall time covered by one pixel — the natural tolerance for merging invisible gaps. */
export function msPerPixel(win: WallRange, pxWidth: number): number {
  if (pxWidth <= 0) return 0
  return Math.max(0, (win.endMs - win.startMs) / pxWidth)
}

// Steps chosen so labels never collide: each is a round unit an operator can read at a glance.
const STEPS = [
  SECOND_MS, 5 * SECOND_MS, 15 * SECOND_MS, 30 * SECOND_MS,
  MINUTE_MS, 2 * MINUTE_MS, 5 * MINUTE_MS, 10 * MINUTE_MS, 15 * MINUTE_MS, 30 * MINUTE_MS,
  HOUR_MS, 2 * HOUR_MS, 3 * HOUR_MS, 6 * HOUR_MS, 12 * HOUR_MS,
  DAY_MS, 2 * DAY_MS, 7 * DAY_MS,
]

const MIN_PX_BETWEEN_DENSE = 70
const SPARSE_MULTIPLIER = 4

function stepAtLeast(minMs: number): number {
  for (const s of STEPS) if (s >= minMs) return s
  return STEPS[STEPS.length - 1]
}

export function pickTickStep(
  win: WallRange,
  pxWidth: number
): { denseStepMs: number; sparseStepMs: number; majorStepMs: number } {
  const perPx = msPerPixel(win, pxWidth)
  const denseStepMs = stepAtLeast(perPx * MIN_PX_BETWEEN_DENSE)
  const sparseStepMs = stepAtLeast(denseStepMs * SPARSE_MULTIPLIER)
  const majorStepMs = stepAtLeast(sparseStepMs * 2)
  return { denseStepMs, sparseStepMs, majorStepMs }
}

const pad = (n: number) => String(n).padStart(2, '0')

function timeLabel(ms: number, stepMs: number): string {
  const d = new Date(ms)
  if (stepMs >= DAY_MS) return dayKeyLocal(ms)
  if (stepMs >= MINUTE_MS) return `${pad(d.getHours())}:${pad(d.getMinutes())}`
  return `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`
}

function coveredAt(segments: readonly Segment[], ms: number): boolean {
  for (const s of segments) {
    if (ms >= s.startMs && ms < s.endMs) return true
    if (s.startMs > ms) break
  }
  return false
}

/**
 * Ticks for the visible window.
 *
 * `edge` ticks mark where a recording starts or stops next to a long gap — without them a stretch
 * of archive floating in empty space has no readable boundary time.
 */
export function buildTicks(win: WallRange, pxWidth: number, segments: readonly Segment[]): Tick[] {
  const span = win.endMs - win.startMs
  if (span <= 0 || pxWidth <= 0) return []

  const { denseStepMs, sparseStepMs, majorStepMs } = pickTickStep(win, pxWidth)
  const singleDay = isSameLocalDay(win.startMs, win.endMs)

  const out: Tick[] = []
  const seen = new Set<number>()

  const push = (ms: number, kind: TickKind, stepMs: number, showDate: boolean) => {
    if (ms < win.startMs || ms > win.endMs || seen.has(ms)) return
    seen.add(ms)
    out.push({ ms, kind, label: showDate ? dayKeyLocal(ms) : timeLabel(ms, stepMs), showDate })
  }

  // Regular ruler. Step depends on whether that spot has archive under it.
  let cursor = Math.ceil(win.startMs / denseStepMs) * denseStepMs
  while (cursor <= win.endMs) {
    const dense = coveredAt(segments, cursor)
    const step = dense ? denseStepMs : sparseStepMs

    if (dense || cursor % sparseStepMs === 0) {
      const kind: TickKind = cursor % majorStepMs === 0 ? 'major' : 'minor'
      push(cursor, kind, step, false)
    }
    cursor += denseStepMs
  }

  // Day boundaries carry the date — and they are the only ticks that do, unless the whole window
  // sits inside one day, in which case the window's first tick states which day that is.
  let day = localDayStartMs(win.startMs)
  if (day < win.startMs) day += DAY_MS
  while (day <= win.endMs) {
    seen.delete(day)
    out.push({ ms: day, kind: 'major', label: dayKeyLocal(day), showDate: true })
    seen.add(day)
    day = localDayStartMs(day + DAY_MS + HOUR_MS)
  }

  if (singleDay && out.length > 0) {
    const first = out.reduce((a, b) => (a.ms <= b.ms ? a : b))
    first.showDate = true
    first.label = `${dayKeyLocal(first.ms)} ${timeLabel(first.ms, denseStepMs)}`
  }

  // Boundaries of recorded stretches that sit next to a real hole.
  for (const seg of segments) {
    for (const boundary of [seg.startMs, seg.endMs]) {
      if (boundary < win.startMs || boundary > win.endMs) continue
      if (seen.has(boundary)) continue
      seen.add(boundary)
      out.push({ ms: boundary, kind: 'edge', label: timeLabel(boundary, denseStepMs), showDate: false })
    }
  }

  return out.sort((a, b) => a.ms - b.ms)
}
