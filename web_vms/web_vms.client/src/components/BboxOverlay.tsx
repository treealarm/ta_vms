import { useEffect, useState, type RefObject } from 'react'
import { liveDetectionsClient } from '../hooks/liveDetectionsClient'
import BboxCanvas from './bbox/BboxCanvas'
import { boxesNow } from './bbox/liveBoxes'
import type { DrawableBox } from './bbox/bboxDraw'

interface Props {
  videoRef: RefObject<HTMLVideoElement | null>
  cameraId: string
  onFindSimilar?: (box: DrawableBox) => void
  onTrackObject?: (box: DrawableBox) => void
  trackAimsOthers?: boolean
  /** The object automatic tracking is following, drawn gold with the rest dimmed. */
  highlight?: { source: string; trackId: number; kind: string } | null
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
export default function BboxOverlay({
  videoRef, cameraId, onFindSimilar, onTrackObject, trackAimsOthers, highlight,
}: Props) {
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

  // Marked here rather than in boxesNow: which box is being followed is a property of the tracking
  // session, not of the detection feed, and the archive overlay marks its own the same way.
  const marked = !highlight
    ? boxes
    : boxes.map((b) => ({
        ...b,
        highlighted: b.trackId === highlight.trackId
          && b.kind === highlight.kind
          && b.source === highlight.source,
      }))

  return (
    <BboxCanvas
      videoRef={videoRef}
      boxes={marked}
      onFindSimilar={onFindSimilar}
      onTrackObject={onTrackObject}
      trackAimsOthers={trackAimsOthers}
    />
  )
}
