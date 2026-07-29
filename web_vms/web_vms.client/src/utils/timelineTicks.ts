// Tick layout for the wall timeline.
//
// Two rules drive everything here:
//  - a full date belongs only where the day actually changes. Stamping "2026-07-28 14:31" on every
//    tick turns the ruler into a wall of text that is harder to read than no labels at all;
//  - ticks are dense over recorded stretches, where the operator scrubs, and sparse over empty
//    ones, which are only being crossed.

import type { Segment } from './archiveRanges'
import { DAY_MS, HOUR_MS, MINUTE_MS, SECOND_MS, localDayStartMs, dayKeyLocal } from './wallTime'
import type { WallRange } from './wallTime'

export type TickKind = 'major' | 'minor' | 'edge'

export type Tick = {
  ms: number
  label: string
  kind: TickKind
  /** True only where the label must carry the calendar date, not just the time. */
  showDate: boolean
  /**
   * Whether the label is drawn. The mark itself always is — a hairline never collides, but text
   * does, and overlapping timestamps are worse than none.
   */
  showLabel: boolean
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

const MIN_PX_BETWEEN_DENSE = 62
// Empty stretches get every other tick. A larger factor was tried and leaves a whole day carrying
// almost no readings once the dense step is already hours wide.
const SPARSE_MULTIPLIER = 2

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
// Rough on-screen width of a label, in pixels: enough to reserve space without measuring text.
// Deliberately generous — a gap that is slightly too wide costs one label, one too narrow makes
// two timestamps collide, which is exactly what we are here to prevent.
const CHAR_PX = 6.2
const LABEL_GAP_PX = 10

function labelWidthPx(label: string): number {
  return label.length * CHAR_PX + LABEL_GAP_PX
}

// Which labels win when there is not room for all of them. Dates orient the operator, the coarse
// ruler carries the reading, recording boundaries are useful but optional, and fine ticks are the
// first to lose their text.
const PRIORITY: Record<TickKind, number> = { major: 1, edge: 2, minor: 3 }

/**
 * Ticks for the visible window.
 *
 * Marks are laid out first, then labels are assigned greedily by priority so no two ever overlap:
 * at a coarse zoom a recording boundary keeps its mark but loses its timestamp rather than printing
 * it on top of the hour beside it.
 */
export function buildTicks(win: WallRange, pxWidth: number, segments: readonly Segment[]): Tick[] {
  const span = win.endMs - win.startMs
  if (span <= 0 || pxWidth <= 0) return []

  const { denseStepMs, sparseStepMs, majorStepMs } = pickTickStep(win, pxWidth)

  const candidates = new Map<number, Tick>()

  const add = (ms: number, kind: TickKind, stepMs: number, showDate: boolean) => {
    if (ms < win.startMs || ms > win.endMs) return
    const existing = candidates.get(ms)
    // A day boundary outranks whatever the regular ruler put at the same instant.
    if (existing && !(showDate && !existing.showDate)) return
    candidates.set(ms, {
      ms,
      kind,
      label: showDate ? dayKeyLocal(ms) : timeLabel(ms, stepMs),
      showDate,
      showLabel: false,
    })
  }

  // Regular ruler. Step depends on whether that spot has archive under it.
  let cursor = Math.ceil(win.startMs / denseStepMs) * denseStepMs
  while (cursor <= win.endMs) {
    const dense = coveredAt(segments, cursor)
    if (dense || cursor % sparseStepMs === 0) {
      const kind: TickKind = cursor % majorStepMs === 0 ? 'major' : 'minor'
      add(cursor, kind, dense ? denseStepMs : sparseStepMs, false)
    }
    cursor += denseStepMs
  }

  // The date appears at day boundaries and nowhere else. Repeating it on every tick makes the ruler
  // unreadable, and inside a single day it says nothing the neighbouring tick does not.
  let day = localDayStartMs(win.startMs)
  if (day < win.startMs) day += DAY_MS
  let dayBoundaries = 0
  while (day <= win.endMs) {
    add(day, 'major', majorStepMs, true)
    dayBoundaries += 1
    day = localDayStartMs(day + DAY_MS + HOUR_MS)
  }

  // When no midnight is on screen there is nothing to say which day this is, so the date goes at
  // the very start of the ruler — as its own marker, not glued onto a time label.
  if (dayBoundaries === 0) add(win.startMs, 'major', majorStepMs, true)

  // Boundaries of recorded stretches.
  for (const seg of segments) {
    add(seg.startMs, 'edge', denseStepMs, false)
    add(seg.endMs, 'edge', denseStepMs, false)
  }

  const ticks = [...candidates.values()].sort((a, b) => a.ms - b.ms)

  // Greedy label placement: highest priority first, each label claiming its horizontal span.
  const claimed: { from: number; to: number }[] = []
  const ordered = [...ticks].sort((a, b) => {
    const pa = a.showDate ? 0 : PRIORITY[a.kind]
    const pb = b.showDate ? 0 : PRIORITY[b.kind]
    return pa !== pb ? pa - pb : a.ms - b.ms
  })

  for (const tick of ordered) {
    const x = msToX(tick.ms, win, pxWidth)
    const from = x
    const to = x + labelWidthPx(tick.label)
    if (to > pxWidth + LABEL_GAP_PX) continue
    if (claimed.some((c) => from < c.to && to > c.from)) continue
    claimed.push({ from, to })
    tick.showLabel = true
  }

  return ticks
}
