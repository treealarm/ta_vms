// Archive coverage as plain wall-clock intervals, plus the seek resolution rules the timeline and
// the wall cells share.
//
// A "segment" here is a stretch of continuous recording, NOT a clip and NOT a server-side
// RecordingRange: the server caps a range at 3 minutes regardless of continuity (see ClipStorage
// DefaultMaxRecordingRangeWallSpan), so ranges must be merged before anything reasons about gaps.

import type { RecordingRangeDto } from '../dto/dtos'
import { isSameLocalDay, localDayEndMs, localDayStartMs } from './wallTime'

export type Segment = { startMs: number; endMs: number }

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

export function clampIntoSegment(seg: Segment, ms: number): number {
  if (ms < seg.startMs) return seg.startMs
  if (ms >= seg.endMs) return Math.max(seg.startMs, seg.endMs - 1)
  return ms
}

/** Coverage of several cameras collapsed into one track ("all cameras on the wall"). */
export function unionSegments(lists: readonly (readonly Segment[])[]): Segment[] {
  const all: Segment[] = []
  for (const list of lists) all.push(...list)
  return mergeSegments(all, 0)
}

export function lastCoveredMs(segs: readonly Segment[]): number | null {
  if (segs.length === 0) return null
  const last = segs[segs.length - 1]
  return Math.max(last.startMs, last.endMs - 1)
}

export function hasCoverageOnDay(segs: readonly Segment[], ms: number): boolean {
  return segs.some((s) => isSameLocalDay(s.startMs, ms) || (s.startMs <= ms && s.endMs > ms))
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
