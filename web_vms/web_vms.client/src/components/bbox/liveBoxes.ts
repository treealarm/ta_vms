import type { AnalyticsDetectionSource } from '../../dto/dtos'
import { HISTORY_MS } from '../../hooks/liveDetectionsClient'
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
 * out — over a lifetime taken from how often the producer actually reports, measured below.
 */

/**
 * How long a box stays after its producer went quiet.
 *
 * Measured, not assumed. This used to be a constant per source — 1.5 s for CV, on the stated
 * grounds that "the CV detector re-emits a live track several times a second". That is a property
 * of the watch, not of the detector: `analytics_watch.sample_fps` defaults to 1, and the archive
 * of this deployment puts the real spacing at a median of 1.000 s. So the box went solid for a
 * second, spent the next half fading, and vanished at 1.5 s — while the sample that would have
 * renewed it was due at 1.0 s and often later. Every box pulsed once a second.
 *
 * A track is therefore given a lifetime of `MISSES_TOLERATED` times its own observed interval: it
 * stays solid until the next report is due, then fades over the interval after that, so what the
 * fade means is "this is now overdue" rather than a fixed number of milliseconds. Reports arriving
 * on time keep it at full opacity and it never blinks; a producer that has genuinely stopped is
 * gone in two of its own intervals, whatever those are.
 *
 * The same shape as the archive's temporal-ROI hold (`roitrc/ta_roienc`
 * `src/adaptive/temporal_roi.cpp`), for the same reason: one setting cannot serve producers running
 * at different rates, so the duration is derived from the track's own samples instead.
 */
const MISSES_TOLERATED = 2

/**
 * The interval assumed until a track has been seen twice and its own can be measured. Doubled by
 * MISSES_TOLERATED these are the lifetimes this file used before anything was measured, so a
 * first sighting behaves exactly as it always did.
 */
const FALLBACK_INTERVAL_MS: Record<AnalyticsDetectionSource, number> = { cv: 750, onvif: 1500 }
const DEFAULT_INTERVAL_MS = 750

/**
 * Floor on the lifetime. A fast producer would otherwise get a lifetime so short that one dropped
 * report blinks the box out; below this the reasoning above stops paying for itself and the old
 * behaviour is the better answer.
 */
const MIN_TTL_MS = 1500

/**
 * Two appearances closer together than this are one sighting, not two.
 *
 * A producer that stamps each detection separately splits one video frame across several
 * LiveFrames, and a duplicate detection of the same track lands twice in the same instant — both
 * have been seen in this deployment. Either would otherwise contribute a near-zero gap to the
 * measurement and halve the interval it is supposed to reveal.
 */
const SIGHTING_EPSILON_MS = 50

/**
 * A box's identity within a source: a person and the face on the same track share a trackId but are
 * distinct boxes.
 */
const keyOf = (b: { trackId: number; kind: string }) => `${b.trackId}|${b.kind}`

/**
 * Median gap between sightings, or null when there are not two of them.
 *
 * Median rather than mean because the outliers are one-sided and large: a track the detector lost
 * for a while contributes a single enormous gap — the archive holds one of 39 s against a median of
 * 1.0 s — and a mean would let that one gap keep every later box on screen long after it was gone.
 */
function medianGap(times: number[]): number | null {
  const gaps: number[] = []
  for (let i = 1; i < times.length; i++) gaps.push(times[i] - times[i - 1])
  if (gaps.length === 0) return null
  gaps.sort((a, b) => a - b)
  const mid = gaps.length >> 1
  return gaps.length % 2 === 1 ? gaps[mid] : (gaps[mid - 1] + gaps[mid]) / 2
}

/** Solid until the next report is due, then fading over the interval after that. */
function opacityFor(age: number, ttl: number): number {
  const solidUntil = ttl / MISSES_TOLERATED
  if (age <= solidUntil) return 1
  if (age >= ttl) return 0
  return (ttl - age) / (ttl - solidUntil)
}

function boxesNowForSource(
  frames: LiveFrame[],
  source: AnalyticsDetectionSource,
  nowPerf: number
): DrawableBox[] {
  // Each box ages on its own, from the frame that last carried it — not from the newest frame in
  // the buffer. Two things depend on that. A producer that stamps each detection separately puts
  // the objects of one video frame into several LiveFrames, and taking only the last would draw one
  // box out of a crowded scene. And a track merely omitted from one report has not gone away; it
  // must fade on its own schedule, which is the behaviour the per-track TTL always had.
  //
  // Oldest first, because the sighting times have to come out in order for the gaps between them to
  // mean anything. The whole buffer is walked rather than stopping early at a lifetime, since the
  // lifetime is now per track and not known until its sightings have been counted; the buffer holds
  // at most HISTORY_MS of frames, so this is a few dozen at worst.
  type Seen = { times: number[]; box: LiveFrame['boxes'][number]; lastArrivedAt: number }
  const seen = new Map<string, Seen>()
  for (const frame of frames) {
    for (const box of frame.boxes) {
      const key = keyOf(box)
      const entry = seen.get(key)
      if (!entry) {
        seen.set(key, { times: [frame.arrivedAt], box, lastArrivedAt: frame.arrivedAt })
        continue
      }
      entry.box = box
      entry.lastArrivedAt = frame.arrivedAt
      if (frame.arrivedAt - entry.times[entry.times.length - 1] >= SIGHTING_EPSILON_MS)
        entry.times.push(frame.arrivedAt)
    }
  }

  const fallback = FALLBACK_INTERVAL_MS[source] ?? DEFAULT_INTERVAL_MS

  const out: DrawableBox[] = []
  for (const { box: b, times, lastArrivedAt } of seen.values()) {
    const interval = medianGap(times) ?? fallback
    // Capped by the history it was measured from: a lifetime longer than the buffer would promise
    // to keep drawing a box whose frame has already been discarded, which cannot happen anyway.
    const ttl = Math.min(Math.max(MISSES_TOLERATED * interval, MIN_TTL_MS), HISTORY_MS)
    const opacity = opacityFor(nowPerf - lastArrivedAt, ttl)
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
