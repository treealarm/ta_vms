import type { RefObject } from 'react'
import { useLiveBoxes } from '../hooks/useLiveBoxes'
import BboxCanvas from './bbox/BboxCanvas'
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

/** Draws analytics bounding boxes over the live <video>. */
export default function BboxOverlay({
  videoRef, cameraId, onFindSimilar, onTrackObject, trackAimsOthers, highlight,
}: Props) {
  const boxes = useLiveBoxes(cameraId)

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
