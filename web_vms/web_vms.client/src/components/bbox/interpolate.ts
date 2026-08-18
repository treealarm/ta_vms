import type { ArchiveFrame, FramesBySource } from '../../hooks/archiveMetadataClient'
import type { AnalyticsDetectionSource } from '../../dto/dtos'
import type { DrawableBox } from './bboxDraw'

/**
 * Turns recorded samples into the boxes visible at one instant of archive playback.
 *
 * The archive is sampled at a few hertz, so drawing only exactly-matching samples would make boxes
 * blink; holding the last one instead would make them lag. Interpolating between the neighbouring
 * samples is what keeps a box on its object between them.
 */

/**
 * Beyond this gap between two samples, the box is hidden rather than interpolated. A walking person
 * covers more than their own box width in a second, so a box stretched across a longer gap is not
 * on the object — and a wrong box is worse than no box.
 */
const MAX_INTERP_GAP_MS = 1000

/**
 * How long a track visible in only one of the two neighbouring samples is still drawn. Without it,
 * a track appearing or disappearing would fade across the whole interval instead of at its own
 * moment.
 */
const HOLD_MS = 300

function lerp(a: number, b: number, t: number): number {
  return a + (b - a) * t
}

/**
 * Boxes from one source at `tMs`, or an empty list when the source has nothing to say there.
 *
 * Exported because live and archive want different composition rules over the same maths: the
 * archive lets ONVIF displace CV (see boxesAt), while the live overlay draws both at once and tells
 * them apart by the dash pattern.
 */
export function boxesAtForSource(
  frames: ArchiveFrame[],
  source: AnalyticsDetectionSource,
  tMs: number
): DrawableBox[] {
  if (frames.length === 0) return []

  // Binary search for the last frame at or before tMs.
  let lo = 0
  let hi = frames.length - 1
  let prevIdx = -1
  while (lo <= hi) {
    const mid = (lo + hi) >> 1
    if (frames[mid].tsMs <= tMs) {
      prevIdx = mid
      lo = mid + 1
    } else {
      hi = mid - 1
    }
  }

  const prev = prevIdx >= 0 ? frames[prevIdx] : null
  const next = prevIdx + 1 < frames.length ? frames[prevIdx + 1] : null

  // Outside the recorded span: hide rather than extrapolate. A box invented past the last sample
  // would sit on whatever the object did next, which nobody recorded.
  if (!prev) return next && next.tsMs - tMs <= HOLD_MS ? asDrawable(next, source, tMs) : []
  if (!next) return tMs - prev.tsMs <= HOLD_MS ? asDrawable(prev, source, tMs) : []

  const gap = next.tsMs - prev.tsMs
  if (gap > MAX_INTERP_GAP_MS) {
    // A real hole in the recording. Show whichever end is close enough to still be current.
    if (tMs - prev.tsMs <= HOLD_MS) return asDrawable(prev, source, tMs)
    if (next.tsMs - tMs <= HOLD_MS) return asDrawable(next, source, tMs)
    return []
  }

  const t = gap === 0 ? 0 : (tMs - prev.tsMs) / gap
  const nextByTrack = new Map(next.boxes.map((b) => [`${b.trackId}|${b.kind}`, b]))
  const out: DrawableBox[] = []

  for (const a of prev.boxes) {
    const key = `${a.trackId}|${a.kind}`
    const b = nextByTrack.get(key)
    if (!b) {
      // Track ended between the samples — keep it only for as long as a real sighting would last.
      if (tMs - prev.tsMs <= HOLD_MS) out.push(toDrawable(a, source, tMs))
      continue
    }
    nextByTrack.delete(key)
    out.push({
      trackId: a.trackId,
      // The nearer sample owns everything that is not geometry: interpolating a plate number or a
      // class label is meaningless.
      ...pickNearest(a, b, t),
      bboxX: lerp(a.bboxX, b.bboxX, t),
      bboxY: lerp(a.bboxY, b.bboxY, t),
      bboxWidth: lerp(a.bboxWidth, b.bboxWidth, t),
      bboxHeight: lerp(a.bboxHeight, b.bboxHeight, t),
      source,
      opacity: 1,
      timeMs: tMs,
    })
  }

  // Tracks that only appear in the later sample.
  for (const b of nextByTrack.values()) {
    if (next.tsMs - tMs <= HOLD_MS) out.push(toDrawable(b, source, tMs))
  }

  return out
}

type Sampled = ArchiveFrame['boxes'][number] & { objectId?: string | null }

function pickNearest(a: Sampled, b: Sampled, t: number): Pick<DrawableBox, 'kind' | 'confidence' | 'label' | 'plateText'> {
  const near = t < 0.5 ? a : b
  return { kind: near.kind, confidence: near.confidence, label: near.label, plateText: near.plateText }
}

function toDrawable(box: Sampled, source: AnalyticsDetectionSource, tMs: number): DrawableBox {
  return {
    trackId: box.trackId,
    kind: box.kind,
    confidence: box.confidence,
    bboxX: box.bboxX,
    bboxY: box.bboxY,
    bboxWidth: box.bboxWidth,
    bboxHeight: box.bboxHeight,
    label: box.label,
    plateText: box.plateText,
    objectId: box.objectId,
    source,
    opacity: 1,
    timeMs: tMs,
  }
}

function asDrawable(frame: ArchiveFrame, source: AnalyticsDetectionSource, tMs: number): DrawableBox[] {
  return frame.boxes.map((b) => toDrawable(b, source, tMs))
}

/**
 * Boxes to draw at `tMs`, with ONVIF taking precedence over CV.
 *
 * The precedence is over the whole set, not per track: the two sources number their tracks
 * independently, so mixing them puts two boxes on one person, which an operator reads as a bug.
 * ONVIF wins because its timestamps come off the same wall clock that bounds the clips, whereas the
 * CV detector stamps after inference on another host — so its boxes can sit behind the frame.
 */
export function boxesAt(framesBySource: FramesBySource, tMs: number): DrawableBox[] {
  const onvif = boxesAtForSource(framesBySource.onvif ?? [], 'onvif', tMs)
  if (onvif.length > 0) return onvif
  return boxesAtForSource(framesBySource.cv ?? [], 'cv', tMs)
}
