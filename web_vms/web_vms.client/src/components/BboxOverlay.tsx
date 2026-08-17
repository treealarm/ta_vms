import { useMemo, type RefObject } from 'react'
import { useLiveDetections } from '../hooks/useLiveDetections'
import BboxCanvas from './bbox/BboxCanvas'
import type { DrawableBox } from './bbox/bboxDraw'

interface Props {
  videoRef: RefObject<HTMLVideoElement | null>
  cameraId: string
}

/**
 * Draws analytics bounding boxes over the live <video>.
 *
 * The live adapter of the shared overlay: it turns the SSE stream's TTL-aged detections into
 * drawable boxes and hands them to BboxCanvas. The TTL/fade model stays here rather than in the
 * shared layer because it is an artefact of *live* — with no next sample to look at, the only way
 * to know a box is stale is to time it out. The archive overlay knows both neighbours and
 * interpolates instead.
 *
 * The detection hook lives here (not in CameraStream) so frequent detection updates re-render only
 * this component, leaving the <video> element's subtree untouched.
 */
export default function BboxOverlay({ videoRef, cameraId }: Props) {
  const detections = useLiveDetections(cameraId)

  const boxes = useMemo<DrawableBox[]>(
    () =>
      detections.map((d) => ({
        trackId: d.trackId,
        kind: d.kind,
        confidence: d.confidence,
        bboxX: d.bboxX,
        bboxY: d.bboxY,
        bboxWidth: d.bboxWidth,
        bboxHeight: d.bboxHeight,
        source: d.source,
        label: d.label,
        plateText: d.plateText,
        objectId: d.objectId,
        opacity: d.opacity,
        timeMs: Date.parse(d.detectedAt),
      })),
    [detections]
  )

  return <BboxCanvas videoRef={videoRef} boxes={boxes} />
}
