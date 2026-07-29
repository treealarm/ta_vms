import { useEffect, useRef, useState } from 'react'

import { wallClockBus } from './wallClockBus'
import type { Segment } from '../utils/archiveRanges'
import type { PlaylistMap } from '../utils/playlistTime'
import { decideFollow } from '../utils/followDecision'

const TICK_MS = 1000

export type FollowStatus = 'idle' | 'following' | 'holding' | 'noCoverage'

type Args = {
  videoRef: React.RefObject<HTMLVideoElement | null>
  slotKey: string
  armed: boolean
  map: PlaylistMap | null
  segments: readonly Segment[]
  nativeHls: boolean
  /** Asks the owner component to rebuild its playlist around this wall instant. */
  onRebuild: (aroundWallMs: number) => void
}

/**
 * Keeps a follower cell on the wall's shared playhead.
 *
 * The playhead is read through a ref and evaluated on a timer rather than in render: it moves
 * several times a second, and re-rendering a video cell at that rate is what makes a wall stutter.
 * State is set only when the operator-visible status actually changes.
 *
 * The clock owner is exempt — it is the one driving the playhead, so following it would be circular.
 */
export function useWallFollow({
  videoRef,
  slotKey,
  armed,
  map,
  segments,
  nativeHls,
  onRebuild,
}: Args): FollowStatus {
  const [status, setStatus] = useState<FollowStatus>('idle')

  const playheadRef = useRef<number | null>(null)
  const unchangedTicksRef = useRef(0)
  const lastSeenPlayheadRef = useRef<number | null>(null)
  const lastRebuildAtRef = useRef(0)

  // Latest values for the timer, without making it a dependency-driven effect that restarts every
  // time the playlist map or coverage is replaced.
  const latest = useRef({ armed, map, segments, nativeHls, onRebuild })
  useEffect(() => {
    latest.current = { armed, map, segments, nativeHls, onRebuild }
  }, [armed, map, segments, nativeHls, onRebuild])

  useEffect(() => wallClockBus.subscribePlayhead((ms) => { playheadRef.current = ms }), [])

  useEffect(() => {
    const timer = window.setInterval(() => {
      const video = videoRef.current
      if (!video) return

      const isOwner = wallClockBus.isOwner(slotKey)
      const playheadMs = playheadRef.current
      if (playheadMs === lastSeenPlayheadRef.current) unchangedTicksRef.current += 1
      else unchangedTicksRef.current = 0
      lastSeenPlayheadRef.current = playheadMs

      const { armed: isArmed, map: pMap, segments: segs, nativeHls: isNative, onRebuild: rebuild } =
        latest.current

      const action = decideFollow({
        isOwner,
        playheadMs,
        playheadUnchangedTicks: unchangedTicksRef.current,
        armed: isArmed,
        map: pMap,
        mediaSec: video.currentTime,
        paused: video.paused,
        segments: segs,
        msSinceLastRebuild: Date.now() - lastRebuildAtRef.current,
        nativeHls: isNative,
      })

      switch (action.kind) {
        case 'none':
          if (isOwner) setStatus('following')
          break
        case 'play':
          void video.play().catch(() => {})
          setStatus('following')
          break
        case 'pause':
          if (!video.paused) video.pause()
          setStatus(action.reason === 'masterStalled' ? 'following' : 'holding')
          break
        case 'seek':
          video.currentTime = action.mediaSec
          if (isArmed && video.paused) void video.play().catch(() => {})
          setStatus('following')
          break
        case 'rebuild':
          lastRebuildAtRef.current = Date.now()
          rebuild(action.aroundWallMs)
          setStatus('following')
          break
        case 'noCoverage':
          if (!video.paused) video.pause()
          setStatus('noCoverage')
          break
      }
    }, TICK_MS)

    return () => clearInterval(timer)
  }, [videoRef, slotKey])

  return status
}
