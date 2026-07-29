// What a follower cell should do to stay on the wall's shared playhead.
//
// Kept as one pure function on purpose: this is a six-way decision evaluated once a second by every
// cell on the wall, and spread across effects it becomes impossible to reason about — the failure
// modes are rebuild storms, cells racing ahead through gaps, and followers frozen forever.
//
// The caller applies the result to the <video>; nothing here touches the DOM or the store.

import type { Segment } from './archiveRanges'
import { segmentAt, nextSegmentAfter } from './archiveRanges'
import type { PlaylistMap } from './playlistTime'
import { mediaTimeAtWallMs, playlistWallBounds } from './playlistTime'

/** Drift below this is left alone — chasing it would make playback stutter continuously. */
export const FOLLOW_TOLERANCE_SEC = 3.5

/** Minimum spacing between playlist rebuilds for one cell. */
export const REBUILD_COOLDOWN_MS = 5_000

/** Consecutive unchanged playhead samples that mean the master is not advancing. */
export const STALL_TICKS = 2

/**
 * How close to the end of its own playlist the clock owner may get before extending it.
 *
 * The owner is exempt from following (it *is* the playhead), but its playlist is a bounded window
 * ending in #EXT-X-ENDLIST. Without this it simply stops at that end, and every follower then sees
 * a frozen playhead and pauses — the whole wall dies a few minutes after each seek.
 */
export const OWNER_REBUILD_LEAD_MS = 45_000

export type FollowInput = {
  playheadMs: number | null
  /** How many consecutive ticks the playhead has not moved. */
  playheadUnchangedTicks: number
  armed: boolean
  map: PlaylistMap | null
  mediaSec: number
  paused: boolean
  /** This camera's archive coverage. */
  segments: readonly Segment[]
  msSinceLastRebuild: number
  /** Safari-style native HLS: no fragment list, so PDT mapping is unavailable. */
  nativeHls: boolean
  /** This cell drives the shared playhead; it follows nothing, but still has to extend its window. */
  isOwner: boolean
}

export type PauseReason = 'masterStalled' | 'holdAtRangeEnd' | 'hole'

export type FollowAction =
  | { kind: 'none' }
  | { kind: 'play' }
  | { kind: 'pause'; reason: PauseReason }
  | { kind: 'seek'; mediaSec: number }
  | { kind: 'rebuild'; aroundWallMs: number }
  | { kind: 'noCoverage' }

export function decideFollow(i: FollowInput): FollowAction {
  if (i.playheadMs === null) return { kind: 'none' }

  // The owner never chases anything, but it must keep material ahead of itself.
  if (i.isOwner) {
    const ownBounds = playlistWallBounds(i.map)
    if (!ownBounds) return { kind: 'none' }
    const nearEnd = i.playheadMs >= ownBounds.endMs - OWNER_REBUILD_LEAD_MS
    return nearEnd && i.msSinceLastRebuild >= REBUILD_COOLDOWN_MS
      ? { kind: 'rebuild', aroundWallMs: i.playheadMs }
      : { kind: 'none' }
  }

  // The master is paused, buffering, or sitting at the end of its own material. Followers hold
  // position rather than running on without it.
  if (i.playheadUnchangedTicks >= STALL_TICKS) return { kind: 'pause', reason: 'masterStalled' }

  // This camera simply has nothing recorded at the wall's current instant.
  if (!segmentAt(i.segments, i.playheadMs)) return { kind: 'noCoverage' }

  // Without a fragment list there is no way to map wall time to media time. Keep it coarse: play
  // when armed and never park the cell in a hold it cannot be woken from.
  if (i.nativeHls) {
    return i.armed && i.paused ? { kind: 'play' } : { kind: 'none' }
  }

  const bounds = playlistWallBounds(i.map)
  if (!bounds) return { kind: 'none' }

  // The playhead has moved outside what this cell has loaded — it needs a different part of the
  // archive. Rebuilding is expensive, so it is rate limited rather than retried every tick.
  if (i.playheadMs < bounds.startMs || i.playheadMs >= bounds.endMs) {
    return i.msSinceLastRebuild >= REBUILD_COOLDOWN_MS
      ? { kind: 'rebuild', aroundWallMs: i.playheadMs }
      : { kind: 'none' }
  }

  const targetSec = mediaTimeAtWallMs(i.map, i.playheadMs)

  // Inside the loaded span but between fragments: a real hole in this camera's recording. Rebuilding
  // would not conjure footage that was never written, and doing it every tick is a request storm —
  // so the cell waits here until the playhead reaches material it actually has.
  if (targetSec === null) {
    const next = nextSegmentAfter(i.segments, i.playheadMs)
    return { kind: 'pause', reason: next ? 'holdAtRangeEnd' : 'hole' }
  }

  const driftSec = targetSec - i.mediaSec
  if (Math.abs(driftSec) <= FOLLOW_TOLERANCE_SEC) {
    return i.armed && i.paused ? { kind: 'play' } : { kind: 'none' }
  }

  return { kind: 'seek', mediaSec: targetSec }
}
