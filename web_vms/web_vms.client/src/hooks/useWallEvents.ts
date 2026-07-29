import { useEffect, useMemo } from 'react'

import { useAppDispatch, useAppSelector } from '../store'
import { fetchWallEvents, selectWallEvents, wallEventsKey, type WallEvent } from '../store/wallEventsSlice'

const DEBOUNCE_MS = 400

// Module-level so several consumers of the same window collapse into one request.
const inFlight = new Set<string>()
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
  useEffect(() => {
    if (!query || cached || inFlight.has(key)) return
    const timer = window.setTimeout(() => {
      if (inFlight.has(key)) return
      inFlight.add(key)
      // Released on failure too, so a transient error does not make the camera look like it had no
      // alarms in this window until the operator pans away and back.
      void dispatch(fetchWallEvents(query)).finally(() => inFlight.delete(key))
    }, DEBOUNCE_MS)
    return () => clearTimeout(timer)
  }, [dispatch, query, key, cached])

  return cached ?? EMPTY
}
