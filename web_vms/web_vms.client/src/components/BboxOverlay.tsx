import { useEffect, useState, type RefObject } from 'react'
import { liveDetectionsClient } from '../hooks/liveDetectionsClient'
import BboxCanvas from './bbox/BboxCanvas'
import { boxesNow } from './bbox/extrapolate'
import type { DrawableBox } from './bbox/bboxDraw'

interface Props {
  videoRef: RefObject<HTMLVideoElement | null>
  cameraId: string
}

/**
 * Draws analytics bounding boxes over the live <video>.
 *
 * Boxes are drawn the moment they arrive and then carried forward along their last known velocity,
 * so they glide between samples instead of stepping. Deliberately NOT the archive's interpolation:
 * that needs the sample after the instant being drawn, which on a live stream only exists if the
 * picture is held back to wait for it. Leading the object beats trailing it — see extrapolate.ts.
 */
export default function BboxOverlay({ videoRef, cameraId }: Props) {
  const [boxes, setBoxes] = useState<DrawableBox[]>([])

  useEffect(() => {
    let raf = 0
    let disposed = false

    // The subscription exists to keep the shared SSE stream open for this camera; the redraw is
    // driven by the animation frame instead, because the box positions keep advancing between
    // samples rather than only when one arrives.
    const unsubscribe = liveDetectionsClient.subscribeFrames(cameraId, () => {})

    const tick = () => {
      if (disposed) return
      const next = boxesNow(liveDetectionsClient.framesFor(cameraId), performance.now())
      setBoxes((prev) => (prev.length === 0 && next.length === 0 ? prev : next))
      raf = requestAnimationFrame(tick)
    }
    raf = requestAnimationFrame(tick)

    return () => {
      disposed = true
      cancelAnimationFrame(raf)
      unsubscribe()
    }
  }, [cameraId])

  return <BboxCanvas videoRef={videoRef} boxes={boxes} />
}
