import { useEffect, useState, type RefObject } from 'react'
import { liveDetectionsClient } from '../hooks/liveDetectionsClient'
import BboxCanvas from './bbox/BboxCanvas'
import { boxesNow } from './bbox/liveBoxes'
import type { DrawableBox } from './bbox/bboxDraw'

interface Props {
  videoRef: RefObject<HTMLVideoElement | null>
  cameraId: string
}

/** How often boxes are re-evaluated so an ageing one fades and then disappears on time. */
const FADE_TICK_MS = 120

/**
 * Draws analytics bounding boxes over the live <video>.
 *
 * Redrawn when a sample arrives, plus a slow tick for the fade-out — not on every animation frame:
 * the boxes do not move between samples (see liveBoxes.ts), so a per-frame loop would repaint an
 * unchanged canvas for every cell of the wall.
 */
export default function BboxOverlay({ videoRef, cameraId }: Props) {
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

  return <BboxCanvas videoRef={videoRef} boxes={boxes} />
}
