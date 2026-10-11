import { useEffect, useState } from 'react'
import { liveDetectionsClient } from './liveDetectionsClient'
import { boxesNow } from '../components/bbox/liveBoxes'
import type { DrawableBox } from '../components/bbox/bboxDraw'

/** How often boxes are re-evaluated so an ageing one fades and then disappears on time. */
const FADE_TICK_MS = 120

/**
 * A camera's live boxes as they should look right now, for whatever draws them — over the video or
 * on the map.
 *
 * Re-evaluated when a sample arrives, plus a slow tick for the fade-out — not on every animation
 * frame: the boxes do not move between samples (see liveBoxes.ts), so a per-frame loop would redraw
 * unchanged boxes for every camera on screen.
 */
export function useLiveBoxes(cameraId: string): DrawableBox[] {
  const [boxes, setBoxes] = useState<DrawableBox[]>([])

  useEffect(() => {
    const refresh = () => {
      const next = boxesNow(liveDetectionsClient.framesFor(cameraId), performance.now())
      setBoxes((prev) => (prev.length === 0 && next.length === 0 ? prev : next))
    }

    const unsubscribe = liveDetectionsClient.subscribeFrames(cameraId, refresh)
    const timer = window.setInterval(refresh, FADE_TICK_MS)
    refresh()

    return () => {
      unsubscribe()
      clearInterval(timer)
    }
  }, [cameraId])

  return boxes
}
