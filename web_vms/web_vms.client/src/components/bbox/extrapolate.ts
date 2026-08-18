import type { AnalyticsDetectionSource } from '../../dto/dtos'
import type { LiveFrame, LiveFramesBySource } from '../../hooks/liveDetectionsClient'
import type { DrawableBox } from './bboxDraw'

/**
 * Smooths live boxes forward in time, without delaying them.
 *
 * The archive interpolates, because it holds the sample on both sides of the instant it draws. Live
 * has no such luxury: the next sample has not happened yet. The only way to interpolate here would
 * be to hold the picture back until it arrives, and that is the wrong trade — a box that leads the
 * object still points at where the object is about to be, while a box that lags points at where it
 * no longer is. So the last known velocity is carried forward instead.
 *
 * Nothing here compares the producer's clock to the browser's. Velocity is a ratio of two producer
 * timestamps, and it is advanced over elapsed local time — both are durations, so the two machines
 * never have to agree on what time it is.
 */

/**
 * How far past the last sample a box may be carried. Roughly one sample interval for a camera that
 * narrates a few times a second: far enough to bridge the gap between samples, short enough that a
 * detector which simply stopped cannot send a box sliding off on its own.
 */
const MAX_EXTRAPOLATE_MS = 400

/**
 * Ceiling on how far extrapolation may displace a box, as a fraction of its own size. A track that
 * jumps — a re-detection, an id reused — would otherwise produce an enormous bogus velocity and
 * fling the box across the frame.
 */
const MAX_DISPLACEMENT_OF_SIZE = 1.5

/**
 * How long a box stays after its producer went quiet, and how long it fades first. The CV detector
 * re-emits a live track several times a second, so 1.5 s means it stopped. A camera's own metadata
 * can legitimately arrive as slowly as once a second, hence the longer grace.
 */
const TTL_BY_SOURCE: Record<AnalyticsDetectionSource, number> = { cv: 1500, onvif: 3000 }
const DEFAULT_TTL_MS = 1500
const FADE_MS = 500

const ttlFor = (source: AnalyticsDetectionSource): number => TTL_BY_SOURCE[source] ?? DEFAULT_TTL_MS

function opacityFor(age: number, ttl: number): number {
  if (age <= ttl - FADE_MS) return 1
  if (age >= ttl) return 0
  return (ttl - age) / FADE_MS
}

// A box's identity within a source: a person and the face on the same track share trackId but are
// distinct boxes.
const keyOf = (b: { trackId: number; kind: string }) => `${b.trackId}|${b.kind}`

function boxesNowForSource(
  frames: LiveFrame[],
  source: AnalyticsDetectionSource,
  nowPerf: number
): DrawableBox[] {
  const last = frames[frames.length - 1]
  if (!last) return []

  const age = nowPerf - last.arrivedAt
  const opacity = opacityFor(age, ttlFor(source))
  if (opacity <= 0) return []

  // The previous frame gives the velocity. Without one there is nothing to carry forward, and the
  // box is simply drawn where it was last seen.
  const prev = frames.length > 1 ? frames[frames.length - 2] : null
  const dtProducer = prev ? last.tsMs - prev.tsMs : 0
  const prevByKey = prev && dtProducer > 0 ? new Map(prev.boxes.map((b) => [keyOf(b), b])) : null

  const advance = Math.min(age, MAX_EXTRAPOLATE_MS)

  return last.boxes.map((b) => {
    let x = b.bboxX
    let y = b.bboxY

    const before = prevByKey?.get(keyOf(b))
    if (before) {
      const vx = (b.bboxX - before.bboxX) / dtProducer
      const vy = (b.bboxY - before.bboxY) / dtProducer
      let dx = vx * advance
      let dy = vy * advance

      const maxDx = b.bboxWidth * MAX_DISPLACEMENT_OF_SIZE
      const maxDy = b.bboxHeight * MAX_DISPLACEMENT_OF_SIZE
      dx = Math.max(-maxDx, Math.min(maxDx, dx))
      dy = Math.max(-maxDy, Math.min(maxDy, dy))

      x += dx
      y += dy
    }

    return {
      trackId: b.trackId,
      kind: b.kind,
      confidence: b.confidence,
      bboxX: x,
      bboxY: y,
      bboxWidth: b.bboxWidth,
      bboxHeight: b.bboxHeight,
      label: b.label,
      plateText: b.plateText,
      objectId: b.objectId,
      source,
      opacity,
      // The producer's own instant for this box, which is what the label should say — not the
      // extrapolated moment, which is this browser's guess rather than an observation.
      timeMs: Date.parse(b.detectedAt),
    } satisfies DrawableBox
  })
}

/**
 * Every box to draw right now. Both sources are drawn together — unlike the archive, where ONVIF
 * displaces CV — because on live they are told apart by the dash pattern and an operator running
 * both expects to see both.
 */
export function boxesNow(frames: LiveFramesBySource, nowPerf: number): DrawableBox[] {
  return [
    ...boxesNowForSource(frames.onvif ?? [], 'onvif', nowPerf),
    ...boxesNowForSource(frames.cv ?? [], 'cv', nowPerf),
  ]
}
