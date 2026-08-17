import { useCallback, useEffect, useMemo, useSyncExternalStore } from 'react'
import { apiFetch } from '../api/apiFetch'
import type {
  AnalyticsDetectionSource,
  ArchiveMetadataBoxDto,
  ArchiveMetadataCoverageDto,
  ArchiveMetadataResponseDto,
} from '../dto/dtos'

/**
 * Recorded analytics geometry for the archive overlay.
 *
 * A module-level store rather than a Redux slice, for the same reason liveDetectionsClient is one:
 * these are frames to draw, not application state. Dispatching on every bucket would re-render the
 * component tree at playback rate, and ArchiveHlsPreview lives outside the monitor's slot state
 * entirely, so it has no slice to belong to.
 */

/**
 * Buckets are one minute, aligned to the minute. The alignment is the point: two seeks into the
 * same minute must hit the same key, or scrubbing re-fetches the same geometry forever.
 */
const BUCKET_MS = 60_000

/** How far around the playhead to keep loaded — one bucket behind, one and a half ahead. */
const PREFETCH_BEFORE_MS = 30_000
const PREFETCH_AFTER_MS = 90_000

/**
 * Deliberately not "the whole loaded playlist window": playlistUrl.ts asks for 1 minute before and
 * 10 minutes after a seek, so following it would pull ten minutes of geometry to draw ten seconds.
 */

/** Buckets kept before the oldest is evicted (~1 hour of archive per camera/stream). */
const MAX_BUCKETS = 60

/**
 * A bucket whose end is within this of now is still being written into, so an empty answer for it
 * is "not yet", not "never".
 */
const LIVE_EDGE_MS = 60_000
const LIVE_EDGE_TTL_MS = 30_000

export type ArchiveBox = ArchiveMetadataBoxDto
export type ArchiveFrame = { tsMs: number; boxes: ArchiveBox[] }
export type FramesBySource = Partial<Record<AnalyticsDetectionSource, ArchiveFrame[]>>

type Bucket = { frames: FramesBySource; fetchedAt: number }

const buckets = new Map<string, Bucket>()
const inFlight = new Set<string>()
const listeners = new Map<string, Set<() => void>>()

/** Bumped whenever any bucket lands, so a subscriber's snapshot changes identity. */
const versions = new Map<string, number>()

function seriesKey(cameraId: string, streamName: string): string {
  return `${cameraId}|${streamName}`
}

function bucketKey(cameraId: string, streamName: string, bucketStartMs: number): string {
  return `${cameraId}|${streamName}|${bucketStartMs}`
}

function notify(series: string) {
  versions.set(series, (versions.get(series) ?? 0) + 1)
  for (const cb of listeners.get(series) ?? []) cb()
}

function bucketStartOf(ms: number): number {
  return Math.floor(ms / BUCKET_MS) * BUCKET_MS
}

function isFresh(bucket: Bucket, bucketStartMs: number): boolean {
  const stillFilling = bucketStartMs + BUCKET_MS > Date.now() - LIVE_EDGE_MS
  if (!stillFilling) return true
  return Date.now() - bucket.fetchedAt < LIVE_EDGE_TTL_MS
}

function evictIfNeeded() {
  while (buckets.size > MAX_BUCKETS) {
    const oldest = buckets.keys().next()
    if (oldest.done) break
    buckets.delete(oldest.value)
  }
}

async function fetchRange(
  cameraId: string,
  streamName: string,
  firstBucketMs: number,
  lastBucketMs: number
): Promise<void> {
  const keys: string[] = []
  for (let b = firstBucketMs; b <= lastBucketMs; b += BUCKET_MS)
    keys.push(bucketKey(cameraId, streamName, b))
  keys.forEach((k) => inFlight.add(k))

  const params = new URLSearchParams({
    cameraId,
    from: new Date(firstBucketMs).toISOString(),
    to: new Date(lastBucketMs + BUCKET_MS).toISOString(),
  })
  if (streamName) params.set('streamName', streamName)

  try {
    const result = await apiFetch<ArchiveMetadataResponseDto>(`/api/arch/metadata?${params}`)

    // Seed every requested bucket, so an empty answer is cached as empty rather than re-requested
    // forever on a camera that has no analytics.
    const fetchedAt = Date.now()
    for (let b = firstBucketMs; b <= lastBucketMs; b += BUCKET_MS)
      buckets.set(bucketKey(cameraId, streamName, b), { frames: {}, fetchedAt })

    for (const source of result?.sources ?? []) {
      for (const frame of source.frames) {
        const tsMs = Date.parse(frame.ts)
        if (!Number.isFinite(tsMs)) continue
        const key = bucketKey(cameraId, streamName, bucketStartOf(tsMs))
        const bucket = buckets.get(key)
        if (!bucket) continue
        const list = (bucket.frames[source.source] ??= [])
        list.push({ tsMs, boxes: frame.boxes })
      }
    }

    // The server orders within a source, but frames land here per bucket and per fan-out result.
    for (let b = firstBucketMs; b <= lastBucketMs; b += BUCKET_MS) {
      const bucket = buckets.get(bucketKey(cameraId, streamName, b))
      if (!bucket) continue
      for (const frames of Object.values(bucket.frames))
        frames?.sort((x, y) => x.tsMs - y.tsMs)
    }

    evictIfNeeded()
    notify(seriesKey(cameraId, streamName))
  } catch {
    // Nothing cached on failure — the next tick asks again.
  } finally {
    keys.forEach((k) => inFlight.delete(k))
  }
}

/** Loads whatever is missing around `playheadMs`, coalescing adjacent gaps into one request. */
function ensureLoaded(cameraId: string, streamName: string, playheadMs: number) {
  const first = bucketStartOf(playheadMs - PREFETCH_BEFORE_MS)
  const last = bucketStartOf(playheadMs + PREFETCH_AFTER_MS)

  let runStart: number | null = null
  for (let b = first; b <= last + BUCKET_MS; b += BUCKET_MS) {
    const key = bucketKey(cameraId, streamName, b)
    const cached = buckets.get(key)
    const missing = b <= last && !inFlight.has(key) && (!cached || !isFresh(cached, b))

    if (missing && runStart === null) runStart = b
    else if (!missing && runStart !== null) {
      void fetchRange(cameraId, streamName, runStart, b - BUCKET_MS)
      runStart = null
    }
  }
}

const EMPTY: FramesBySource = {}

/**
 * Frames covering the buckets currently around the playhead. The returned object changes identity
 * only when a bucket lands, so the overlay can redraw off it without React work per frame.
 */
export function useArchiveMetadata(
  cameraId: string | undefined,
  streamName: string,
  playheadMs: number | null,
  enabled: boolean
): FramesBySource {
  const series = cameraId ? seriesKey(cameraId, streamName) : ''

  const subscribe = useCallback(
    (onChange: () => void) => {
      if (!series) return () => {}
      let set = listeners.get(series)
      if (!set) listeners.set(series, (set = new Set()))
      set.add(onChange)
      return () => {
        set.delete(onChange)
        if (set.size === 0) listeners.delete(series)
      }
    },
    [series]
  )

  // The playhead moves every animation frame, but which buckets it needs changes only when it
  // crosses a minute boundary — so both the prefetch and the merge key off the bucket, not the
  // instant. Keying off playheadMs would re-run this loop sixty times a second per cell.
  const activeBucket =
    playheadMs !== null && Number.isFinite(playheadMs) ? bucketStartOf(playheadMs) : null

  useEffect(() => {
    if (enabled && cameraId && activeBucket !== null)
      ensureLoaded(cameraId, streamName, activeBucket)
  }, [enabled, cameraId, streamName, activeBucket])

  const getSnapshot = useCallback(() => {
    if (!series || !enabled) return 0
    return versions.get(series) ?? 0
  }, [series, enabled])

  const version = useSyncExternalStore(subscribe, getSnapshot)

  return useMemo(() => {
    if (!enabled || !cameraId || activeBucket === null) return EMPTY

    // Only the buckets the playhead can actually reach — the rest of the prefetch is warmth, and
    // interpolation never looks more than a second either way.
    const merged: FramesBySource = {}
    for (let b = activeBucket - BUCKET_MS; b <= activeBucket + BUCKET_MS; b += BUCKET_MS) {
      const bucket = buckets.get(bucketKey(cameraId, streamName, b))
      if (!bucket) continue
      for (const [source, frames] of Object.entries(bucket.frames)) {
        if (!frames?.length) continue
        const key = source as AnalyticsDetectionSource
        merged[key] = merged[key] ? [...merged[key]!, ...frames] : frames
      }
    }
    return merged
    // `version` is the store's change signal: the buckets themselves are mutable module state, so
    // nothing else here would tell React that new frames landed. The rule cannot see that.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [enabled, cameraId, streamName, activeBucket, version])
}

// --- coverage ------------------------------------------------------------------------------

export type MetadataCoverage = 'unknown' | 'none' | 'some'

type CoverageEntry = { value: MetadataCoverage }

const coverage = new Map<string, CoverageEntry>()
const coverageInFlight = new Set<string>()
const coverageListeners = new Map<string, Set<() => void>>()

function coverageKey(cameraId: string, streamName: string, fromMs: number, toMs: number): string {
  return `${cameraId}|${streamName}|${fromMs}|${toMs}`
}

async function fetchCoverage(key: string, cameraId: string, streamName: string, fromMs: number, toMs: number) {
  coverageInFlight.add(key)
  const params = new URLSearchParams({
    cameraId,
    from: new Date(fromMs).toISOString(),
    to: new Date(toMs).toISOString(),
  })
  if (streamName) params.set('streamName', streamName)

  try {
    const result = await apiFetch<ArchiveMetadataCoverageDto>(`/api/arch/metadata/coverage?${params}`)
    coverage.set(key, { value: result?.hasCv || result?.hasOnvif ? 'some' : 'none' })
    for (const cb of coverageListeners.get(key) ?? []) cb()
  } catch {
    // Left unknown: the caller shows the control optimistically rather than hiding it on a hiccup.
  } finally {
    coverageInFlight.delete(key)
  }
}

/**
 * Whether the archive holds any geometry for this camera in this window — which is what decides
 * whether the overlay toggle is worth offering.
 *
 * A property of (camera, stream, time range), not of the camera: the live test
 * (`hasAnalytics || onvifBboxCapable`) describes the camera *now*, and an operator looking at last
 * Tuesday may be looking at a period before the watch existed, or after it was deleted.
 */
export function useArchiveMetadataCoverage(
  cameraId: string | undefined,
  streamName: string,
  windowStartMs: number | null,
  windowEndMs: number | null
): MetadataCoverage {
  const ready =
    !!cameraId &&
    windowStartMs !== null &&
    windowEndMs !== null &&
    Number.isFinite(windowStartMs) &&
    Number.isFinite(windowEndMs) &&
    windowStartMs < windowEndMs
  const key = ready ? coverageKey(cameraId, streamName, windowStartMs, windowEndMs) : ''

  const subscribe = useCallback(
    (onChange: () => void) => {
      if (!key) return () => {}
      let set = coverageListeners.get(key)
      if (!set) coverageListeners.set(key, (set = new Set()))
      set.add(onChange)
      if (!coverage.has(key) && !coverageInFlight.has(key))
        void fetchCoverage(key, cameraId!, streamName, windowStartMs!, windowEndMs!)
      return () => {
        set.delete(onChange)
        if (set.size === 0) coverageListeners.delete(key)
      }
    },
    [key, cameraId, streamName, windowStartMs, windowEndMs]
  )

  const getSnapshot = useCallback(() => (key ? coverage.get(key)?.value ?? 'unknown' : 'unknown'), [key])

  return useSyncExternalStore(subscribe, getSnapshot)
}
