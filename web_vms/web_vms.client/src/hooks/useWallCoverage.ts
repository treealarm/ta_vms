import { useEffect, useMemo, useRef } from 'react'

import { useAppDispatch, useAppSelector } from '../store'
import {
  fetchRecordingTimeline,
  selectWallCoverage,
  selectWallCoverageLoading,
  wallCoverageKey,
} from '../store/wallCoverageSlice'
import type { RecordingTimelineQueryDto } from '../dto/dtos'
import type { Segment } from '../utils/archiveRanges'
import { unionSegments } from '../utils/archiveRanges'

const DEBOUNCE_MS = 300

/** Quantizes the window so panning by a few pixels does not mint a fresh cache key every frame. */
function quantize(ms: number, stepMs: number): number {
  return Math.floor(ms / stepMs) * stepMs
}

export type WallCoverage = {
  byCamera: Record<string, Segment[]>
  /** All wall cameras collapsed into one track. */
  union: Segment[]
  loading: boolean
}

const EMPTY: Record<string, Segment[]> = {}

/**
 * Archive coverage for every camera on the wall over the visible window, in one request.
 *
 * The window is padded and quantized so scrubbing reuses cached responses, and `minGapMs` is tied
 * to the pixel size so the payload cannot grow when the operator zooms out to a month.
 */
export function useWallCoverage(
  cameraIds: readonly string[],
  windowStartMs: number,
  windowWidthMs: number,
  pxWidth: number
): WallCoverage {
  const dispatch = useAppDispatch()

  const query = useMemo<RecordingTimelineQueryDto | null>(() => {
    if (cameraIds.length === 0 || windowWidthMs <= 0) return null

    // Fetch half a window either side so a small pan is already covered.
    const pad = windowWidthMs / 2
    const step = Math.max(60_000, windowWidthMs / 8)
    const from = quantize(windowStartMs - pad, step)
    const to = quantize(windowStartMs + windowWidthMs + pad, step) + step
    const minGapMs = pxWidth > 0 ? Math.max(1000, Math.floor(windowWidthMs / pxWidth)) : 1000

    return {
      cameraIds: [...cameraIds].sort(),
      fromUtc: new Date(from).toISOString(),
      toUtc: new Date(to).toISOString(),
      minGapMs,
    }
  }, [cameraIds, windowStartMs, windowWidthMs, pxWidth])

  const key = query ? wallCoverageKey(query) : ''
  const cached = useAppSelector(selectWallCoverage(key))
  const loading = useAppSelector(selectWallCoverageLoading(key))

  const requestedRef = useRef<Set<string>>(new Set())

  useEffect(() => {
    if (!query || cached || requestedRef.current.has(key)) return

    const timer = window.setTimeout(() => {
      requestedRef.current.add(key)
      void dispatch(fetchRecordingTimeline(query))
    }, DEBOUNCE_MS)

    return () => clearTimeout(timer)
  }, [dispatch, query, key, cached])

  const byCamera = cached ?? EMPTY
  const union = useMemo(() => unionSegments(Object.values(byCamera)), [byCamera])

  return { byCamera, union, loading: loading || (!cached && !!query) }
}
