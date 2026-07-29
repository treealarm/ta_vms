import { useEffect, useMemo, useRef } from 'react'

import { useAppDispatch, useAppSelector } from '../store'
import { fetchWallEvents, selectWallEvents, wallEventsKey, type WallEvent } from '../store/wallEventsSlice'

const DEBOUNCE_MS = 400
const EMPTY: WallEvent[] = []

function quantize(ms: number, stepMs: number): number {
  return Math.floor(ms / stepMs) * stepMs
}

/**
 * Retention events of one camera inside the visible window.
 *
 * The window is padded and quantized like coverage is, so scrubbing reuses cached responses instead
 * of issuing a request per frame.
 */
export function useWallEvents(
  cameraId: string | null,
  windowStartMs: number,
  windowWidthMs: number
): WallEvent[] {
  const dispatch = useAppDispatch()

  const query = useMemo(() => {
    if (!cameraId || windowWidthMs <= 0) return null
    const pad = windowWidthMs / 2
    const step = Math.max(60_000, windowWidthMs / 8)
    return {
      cameraId,
      fromUtc: new Date(quantize(windowStartMs - pad, step)).toISOString(),
      toUtc: new Date(quantize(windowStartMs + windowWidthMs + pad, step) + step).toISOString(),
    }
  }, [cameraId, windowStartMs, windowWidthMs])

  const key = query ? wallEventsKey(query) : ''
  const cached = useAppSelector(selectWallEvents(key))
  const requestedRef = useRef<Set<string>>(new Set())

  useEffect(() => {
    if (!query || cached || requestedRef.current.has(key)) return
    const timer = window.setTimeout(() => {
      requestedRef.current.add(key)
      void dispatch(fetchWallEvents(query))
    }, DEBOUNCE_MS)
    return () => clearTimeout(timer)
  }, [dispatch, query, key, cached])

  return cached ?? EMPTY
}
