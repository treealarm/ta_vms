import type { AnalyticsDetectionSource } from '../../dto/dtos'
import type { LiveFrame, LiveFramesBySource } from '../../hooks/liveDetectionsClient'
import type { DrawableBox } from './bboxDraw'

/**
 * Live boxes, drawn where the producer last reported them.
 *
 * Deliberately no smoothing between samples, having tried both alternatives and found each worse
 * than this:
 *
 *  - Interpolating, as the archive does, needs the sample *after* the instant being drawn. On a
 *    live stream that only exists if the picture is held back to wait for it, and a box that trails
 *    the object is worse than one that is merely a few frames stale.
 *  - Extrapolating along the last velocity needs that velocity to mean something. It does not here:
 *    the samples arrive unevenly, so a pair that happens to land close together yields a huge
 *    Δposition/Δt, and the box lurches ahead and then snaps back when the next sample lands. It
 *    amplifies timestamp jitter instead of smoothing motion, and it visibly looked like it.
 *
 * So the box sits where it was last seen, and the only thing that moves is its opacity as it ages
 * out. Any future attempt at smoothing has to start from a real measurement of the producer's
 * sample spacing, not from the assumption that it is regular.
 */

/**
 * How long a box stays after its producer went quiet, and how long it fades first. The CV detector
 * re-emits a live track several times a second, so 1.5 s means it stopped. A camera's own metadata
 * can legitimately arrive as slowly as once a second, and the same budget there would make every
 * box blink between updates — hence the longer grace for onvif.
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

function boxesNowForSource(
  frames: LiveFrame[],
  source: AnalyticsDetectionSource,
  nowPerf: number
): DrawableBox[] {
  const last = frames[frames.length - 1]
  if (!last) return []

  const opacity = opacityFor(nowPerf - last.arrivedAt, ttlFor(source))
  if (opacity <= 0) return []

  return last.boxes.map(
    (b) =>
      ({
        trackId: b.trackId,
        kind: b.kind,
        confidence: b.confidence,
        bboxX: b.bboxX,
        bboxY: b.bboxY,
        bboxWidth: b.bboxWidth,
        bboxHeight: b.bboxHeight,
        label: b.label,
        plateText: b.plateText,
        objectId: b.objectId,
        source,
        opacity,
        timeMs: Date.parse(b.detectedAt),
      }) satisfies DrawableBox
  )
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
