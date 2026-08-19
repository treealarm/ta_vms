// Archive coverage as plain wall-clock intervals, plus the seek resolution rules the timeline and
// the wall cells share.
//
// A "segment" here is a stretch of continuous recording, NOT a clip and NOT a server-side
// RecordingRange: the server caps a range at 3 minutes regardless of continuity (see ClipStorage
// DefaultMaxRecordingRangeWallSpan), so ranges must be merged before anything reasons about gaps.

import type { RecordingRangeDto } from '../dto/dtos'
import { isSameLocalDay, localDayEndMs, localDayStartMs } from './wallTime'

export type Segment = {
  startMs: number
  endMs: number
  /**
   * Where the footage came from — `edge` for a range recovered off the camera's own storage.
   * Only carried by coverage that is going to be drawn; the interval maths ignores it, because a
   * hole is a hole regardless of who would have filled it.
   */
  origin?: string
  /**
   * Whether the footage has been re-encoded with ROI. Carried alongside `origin` and for the same
   * reason: it only matters to what gets drawn, never to the interval maths.
   */
  roiTranscoded?: boolean
}

/** Clips whose end is unknown are assumed to run until the next clip starts, or 60 s (one clip). */
const ASSUMED_CLIP_MS = 60_000

export function rangesToSegments(
  ranges: readonly RecordingRangeDto[] | undefined,
  streamName?: string
): Segment[] {
  if (!ranges?.length) return []

  const out: Segment[] = []
  for (const range of ranges) {
    for (const rec of range.recordings ?? []) {
      if (streamName && rec.streamName && rec.streamName !== streamName) continue
      const startMs = Date.parse(rec.startTime)
      if (!Number.isFinite(startMs)) continue
      const endParsed = rec.endTime ? Date.parse(rec.endTime) : NaN
      const endMs = Number.isFinite(endParsed) && endParsed > startMs ? endParsed : startMs + ASSUMED_CLIP_MS
      out.push({ startMs, endMs })
    }
  }
  return mergeSegments(out, 0)
}

/**
 * Sorts and coalesces overlapping or near-touching segments. `joinToleranceMs` is the largest gap
 * still treated as continuous — used both to undo the server's artificial 3-minute split and, on
 * the timeline, to drop gaps thinner than one pixel.
 */
export function mergeSegments(segs: readonly Segment[], joinToleranceMs: number): Segment[] {
  if (segs.length === 0) return []

  const sorted = [...segs]
    .filter((s) => Number.isFinite(s.startMs) && Number.isFinite(s.endMs) && s.endMs > s.startMs)
    .sort((a, b) => a.startMs - b.startMs)
  if (sorted.length === 0) return []

  const out: Segment[] = [{ ...sorted[0] }]
  for (let i = 1; i < sorted.length; i++) {
    const cur = sorted[i]
    const last = out[out.length - 1]
    if (cur.startMs - last.endMs <= joinToleranceMs) {
      if (cur.endMs > last.endMs) last.endMs = cur.endMs
    } else {
      out.push({ ...cur })
    }
  }
  return out
}

/**
 * The holes: what `win` covers and `segs` does not.
 *
 * `joinToleranceMs` drops gaps too thin to see — at timeline scale a one-pixel gap is noise, and
 * drawing it makes solid coverage look shredded.
 */
export function complementSegments(
  segs: readonly Segment[],
  win: Segment,
  joinToleranceMs = 0
): Segment[] {
  if (win.endMs <= win.startMs) return []

  const merged = mergeSegments(
    segs.filter((s) => s.endMs > win.startMs && s.startMs < win.endMs),
    joinToleranceMs
  )

  const out: Segment[] = []
  let cursor = win.startMs
  for (const s of merged) {
    const start = Math.max(s.startMs, win.startMs)
    if (start - cursor > joinToleranceMs) out.push({ startMs: cursor, endMs: start })
    cursor = Math.max(cursor, Math.min(s.endMs, win.endMs))
  }
  if (win.endMs - cursor > joinToleranceMs) out.push({ startMs: cursor, endMs: win.endMs })
  return out
}

/** `a` minus `b` — e.g. an outage minus what has already been pulled back from the camera. */
export function subtractSegments(
  a: readonly Segment[],
  b: readonly Segment[],
  joinToleranceMs = 0
): Segment[] {
  const out: Segment[] = []
  for (const seg of mergeSegments(a, 0)) {
    out.push(...complementSegments(b, seg, joinToleranceMs))
  }
  return out
}

/** Overlap of two sets of intervals — e.g. "missing from the archive" against "still on the card". */
export function intersectSegments(a: readonly Segment[], b: readonly Segment[]): Segment[] {
  const left = mergeSegments(a, 0)
  const right = mergeSegments(b, 0)
  const out: Segment[] = []

  let i = 0
  let j = 0
  while (i < left.length && j < right.length) {
    const startMs = Math.max(left[i].startMs, right[j].startMs)
    const endMs = Math.min(left[i].endMs, right[j].endMs)
    if (endMs > startMs) out.push({ startMs, endMs })
    // Advance whichever ends first: the other may still overlap what comes next.
    if (left[i].endMs < right[j].endMs) i += 1
    else j += 1
  }
  return out
}

export function segmentAt(segs: readonly Segment[], ms: number): Segment | null {
  for (const s of segs) {
    if (ms >= s.startMs && ms < s.endMs) return s
    if (s.startMs > ms) break
  }
  return null
}

export function nextSegmentAfter(segs: readonly Segment[], ms: number): Segment | null {
  for (const s of segs) if (s.startMs > ms) return s
  return null
}

export function prevSegmentBefore(segs: readonly Segment[], ms: number): Segment | null {
  let found: Segment | null = null
  for (const s of segs) {
    if (s.endMs <= ms) found = s
    else break
  }
  return found
}

/**
 * Nearest recorded instant to `ms`, or null if everything is further than `maxDistanceMs`.
 * The distance cap is what stops a click in an empty evening from dragging playback to the
 * previous morning.
 */
export function snapToNearest(
  segs: readonly Segment[],
  ms: number,
  maxDistanceMs: number
): number | null {
  const inside = segmentAt(segs, ms)
  if (inside) return ms

  const before = prevSegmentBefore(segs, ms)
  const after = nextSegmentAfter(segs, ms)

  // Land just inside the segment, never exactly on its exclusive end.
  const beforeCandidate = before ? before.endMs - 1 : null
  const afterCandidate = after ? after.startMs : null

  const beforeDist = beforeCandidate === null ? Infinity : ms - beforeCandidate
  const afterDist = afterCandidate === null ? Infinity : afterCandidate - ms

  if (beforeDist <= afterDist && beforeDist <= maxDistanceMs) return beforeCandidate
  if (afterDist <= maxDistanceMs) return afterCandidate
  return null
}

/**
 * Resolve a seek without leaving the calendar day the user is looking at: the closest recording
 * earlier the same local day, otherwise the first one later that day. Returns null when the day
 * has no archive at all — the caller decides what to do, and must NOT silently hop to another day.
 */
export function resolveWithinDay(segs: readonly Segment[], ms: number): number | null {
  if (segmentAt(segs, ms)) return ms

  const dayStart = localDayStartMs(ms)
  const dayEnd = localDayEndMs(ms)
  const sameDay = segs.filter((s) => s.endMs > dayStart && s.startMs < dayEnd)
  if (sameDay.length === 0) return null

  const before = prevSegmentBefore(sameDay, ms)
  if (before) return Math.max(dayStart, before.endMs - 1)

  const after = nextSegmentAfter(sameDay, ms)
  if (after) return Math.max(dayStart, after.startMs)

  return null
}

/**
 * First recorded instant inside [fromMs, toMs), or null when that span holds nothing.
 *
 * Used after picking an hour: the hour strip reports "13:00", but the recording in that hour may
 * start at 13:49, and landing on the hour mark drops the operator into empty time.
 */
export function firstCoveredInRange(
  segs: readonly Segment[],
  fromMs: number,
  toMs: number
): number | null {
  for (const s of segs) {
    if (s.endMs <= fromMs) continue
    if (s.startMs >= toMs) break
    return Math.max(s.startMs, fromMs)
  }
  return null
}

export function clampIntoSegment(seg: Segment, ms: number): number {
  if (ms < seg.startMs) return seg.startMs
  if (ms >= seg.endMs) return Math.max(seg.startMs, seg.endMs - 1)
  return ms
}

/** Coverage of several cameras collapsed into one track ("all cameras on the wall"). */
/** Whether two stretches would be drawn the same way. */
function sameMark(a: Segment, b: Segment): boolean {
  return (a.origin ?? 'live') === (b.origin ?? 'live')
    && (a.roiTranscoded === true) === (b.roiTranscoded === true)
}

/**
 * Every camera's coverage on one lane.
 *
 * Unlike mergeSegments this will not coalesce stretches that are drawn differently — recovered
 * footage is hatched and re-compressed footage is banded, and merging those into a neighbour would
 * silently relabel them. The interval maths keeps ignoring the marks, as the Segment doc says; only
 * this path draws, so only this path has to care.
 *
 * Where cameras genuinely disagree over the same instant the earlier stretch keeps its mark and the
 * later one starts after it. One lane standing for every camera cannot say two things about one
 * moment, and picking one is more honest than merging them into whichever came first.
 */
export function unionSegments(lists: readonly (readonly Segment[])[]): Segment[] {
  const all: Segment[] = []
  for (const list of lists) all.push(...list)

  const sorted = all
    .filter((s) => Number.isFinite(s.startMs) && Number.isFinite(s.endMs) && s.endMs > s.startMs)
    .sort((a, b) => a.startMs - b.startMs)
  if (sorted.length === 0) return []

  const out: Segment[] = [{ ...sorted[0] }]
  for (let i = 1; i < sorted.length; i++) {
    const cur = sorted[i]
    const last = out[out.length - 1]

    if (cur.startMs <= last.endMs && sameMark(cur, last)) {
      if (cur.endMs > last.endMs) last.endMs = cur.endMs
      continue
    }
    const startMs = Math.max(cur.startMs, last.endMs)
    if (cur.endMs > startMs) out.push({ ...cur, startMs })
  }
  return out
}

export function lastCoveredMs(segs: readonly Segment[]): number | null {
  if (segs.length === 0) return null
  const last = segs[segs.length - 1]
  return Math.max(last.startMs, last.endMs - 1)
}

export function hasCoverageOnDay(segs: readonly Segment[], ms: number): boolean {
  return segs.some((s) => isSameLocalDay(s.startMs, ms) || (s.startMs <= ms && s.endMs > ms))
}

/**
 * Window that frames the archive around `ms` instead of a fixed span.
 *
 * Opening the whole day when recording only spans an hour of it wastes most of the width on empty
 * time and squeezes the material into a sliver. This fits the window to the recordings of that
 * local day, with margins — and falls back to the full day when the archive really does run from
 * one end of it to the other, or when there is nothing to fit.
 */
export function fitWindowToDay(
  segs: readonly Segment[],
  ms: number,
  opts: { minWidthMs: number; marginRatio?: number }
): { startMs: number; widthMs: number } {
  const dayStart = localDayStartMs(ms)
  const dayEnd = localDayEndMs(ms)
  const fullDay = { startMs: dayStart, widthMs: dayEnd - dayStart }

  const onDay = segs.filter((s) => s.endMs > dayStart && s.startMs < dayEnd)
  if (onDay.length === 0) return fullDay

  const first = Math.max(dayStart, Math.min(...onDay.map((s) => s.startMs)))
  const last = Math.min(dayEnd, Math.max(...onDay.map((s) => s.endMs)))

  const span = last - first
  const margin = Math.max(opts.minWidthMs * 0.1, span * (opts.marginRatio ?? 0.08))

  let startMs = first - margin
  let endMs = last + margin

  if (endMs - startMs < opts.minWidthMs) {
    const centre = (first + last) / 2
    startMs = centre - opts.minWidthMs / 2
    endMs = centre + opts.minWidthMs / 2
  }

  // Once the fit covers most of the day, the day itself is the tidier frame — its boundaries are
  // meaningful to the operator, arbitrary margins are not.
  if (endMs - startMs >= (dayEnd - dayStart) * 0.8) return fullDay

  return { startMs, widthMs: endMs - startMs }
}

function windowIntersectsCoverage(
  segs: readonly Segment[],
  startMs: number,
  widthMs: number
): boolean {
  const endMs = startMs + widthMs
  return segs.some((s) => s.endMs > startMs && s.startMs < endMs)
}

/**
 * Where to scroll the timeline window so it keeps showing something.
 *
 * Plain panning is used while there is still archive in view; once the window would land on empty
 * time it skips straight to the nearest recording in that direction instead. Archive is mostly
 * holes, so a fixed-step scroller otherwise spends most of its clicks crossing nothing.
 *
 * Returns the new window start, or null when there is no archive that way at all.
 */
export function panWindowToCoverage(
  segs: readonly Segment[],
  windowStartMs: number,
  windowWidthMs: number,
  deltaMs: number
): number | null {
  const naive = windowStartMs + deltaMs
  if (segs.length === 0) return null
  if (windowIntersectsCoverage(segs, naive, windowWidthMs)) return naive

  if (deltaMs > 0) {
    const next = segs.find((s) => s.startMs >= naive)
    // Land with the recording a quarter in, so its lead-in is visible rather than flush at the edge.
    return next ? next.startMs - windowWidthMs / 4 : null
  }

  const naiveEnd = naive + windowWidthMs
  let prev: Segment | null = null
  for (const s of segs) {
    if (s.endMs <= naiveEnd) prev = s
    else break
  }
  return prev ? prev.endMs - (windowWidthMs * 3) / 4 : null
}
