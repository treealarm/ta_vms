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

/**
 * A box's identity within a source: a person and the face on the same track share a trackId but are
 * distinct boxes.
 */
const keyOf = (b: { trackId: number; kind: string }) => `${b.trackId}|${b.kind}`

function boxesNowForSource(
  frames: LiveFrame[],
  source: AnalyticsDetectionSource,
  nowPerf: number
): DrawableBox[] {
  const ttl = ttlFor(source)

  // Each box ages on its own, from the frame that last carried it — not from the newest frame in
  // the buffer. Two things depend on that. A producer that stamps each detection separately puts
  // the objects of one video frame into several LiveFrames, and taking only the last would draw one
  // box out of a crowded scene. And a track merely omitted from one report has not gone away; it
  // must fade on its own schedule, which is the behaviour the per-track TTL always had.
  const latest = new Map<string, { box: LiveFrame['boxes'][number]; arrivedAt: number }>()
  for (let i = frames.length - 1; i >= 0; i--) {
    const frame = frames[i]
    if (nowPerf - frame.arrivedAt >= ttl) break // older frames can only be staler
    for (const box of frame.boxes) {
      const key = keyOf(box)
      if (!latest.has(key)) latest.set(key, { box, arrivedAt: frame.arrivedAt })
    }
  }

  const out: DrawableBox[] = []
  for (const { box: b, arrivedAt } of latest.values()) {
    const opacity = opacityFor(nowPerf - arrivedAt, ttl)
    if (opacity <= 0) continue
    out.push({
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
    })
  }
  return out
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
