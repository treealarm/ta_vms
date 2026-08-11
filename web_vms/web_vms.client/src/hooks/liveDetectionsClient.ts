import type { AnalyticsActivityEntryDto, AnalyticsDetectionSource } from '../dto/dtos'

/** A detection ready to draw: the DTO plus a fade multiplier in [0,1] recomputed as it ages out. */
export type LiveDetection = AnalyticsActivityEntryDto & { opacity: number }

type StoredDetection = AnalyticsActivityEntryDto & { receivedAt: number }
type Listener = (detections: LiveDetection[]) => void

// How long a detection stays on screen after its last update, and how long it fades before that.
// The CV detector re-emits a live track several times a second, so 1.5 s means "the detector went
// quiet". A camera's own metadata can legitimately arrive as slowly as once a second, and the same
// budget there would make every box blink between updates — hence the longer grace for onvif.
const TTL_BY_SOURCE: Record<AnalyticsDetectionSource, number> = { cv: 1500, onvif: 3000 }
const DEFAULT_TTL_MS = 1500
const FADE_MS = 500
const TICK_MS = 120
const RECONNECT_MS = 2000

const ttlFor = (source: AnalyticsDetectionSource | undefined): number =>
  (source && TTL_BY_SOURCE[source]) || DEFAULT_TTL_MS

function opacityFor(receivedAt: number, now: number, ttl: number): number {
  const age = now - receivedAt
  if (age <= ttl - FADE_MS) return 1
  if (age >= ttl) return 0
  return (ttl - age) / FADE_MS
}

// A detection's identity is (source, track, kind): a person and the face on the same track share
// trackId but are distinct boxes, and the CV detector and the camera number their tracks
// independently, so a camera running both would otherwise have them collide.
const keyOf = (d: { trackId: number; kind: string; source: AnalyticsDetectionSource }) =>
  `${d.source}:${d.trackId}:${d.kind}`

// One process-wide SSE connection to /api/analytics/live carries detections for ALL cameras; each
// camera overlay subscribes to just its slice. This is deliberately a singleton, not a per-camera
// connection: browsers cap HTTP/1.1 connections at ~6 per origin, so one stream per camera would
// exhaust the pool on a large monitor grid and stall unrelated requests (state polls, WHEP offers).
// The connection is opened lazily on the first subscriber and closed when the last leaves, so it
// costs nothing when no overlay is on.
class LiveDetectionsClient {
  private cameras = new Map<string, Map<string, StoredDetection>>()
  private listeners = new Map<string, Set<Listener>>()
  private refCount = 0
  private reader?: ReadableStreamDefaultReader<Uint8Array>
  private stopped = true
  private tick?: number
  private reconnectTimer?: number

  subscribe(cameraId: string, cb: Listener): () => void {
    let set = this.listeners.get(cameraId)
    if (!set) {
      set = new Set()
      this.listeners.set(cameraId, set)
    }
    set.add(cb)
    this.refCount += 1
    if (this.refCount === 1) this.start()
    cb(this.snapshot(cameraId)) // deliver current state immediately

    return () => {
      const s = this.listeners.get(cameraId)
      if (s) {
        s.delete(cb)
        if (s.size === 0) this.listeners.delete(cameraId)
      }
      this.refCount -= 1
      if (this.refCount === 0) this.stop()
    }
  }

  private snapshot(cameraId: string): LiveDetection[] {
    const m = this.cameras.get(cameraId)
    if (!m) return []
    const now = performance.now()
    const out: LiveDetection[] = []
    for (const d of m.values()) {
      const opacity = opacityFor(d.receivedAt, now, ttlFor(d.source))
      if (opacity > 0) out.push({ ...d, opacity })
    }
    return out
  }

  private notify(cameraId: string): void {
    const set = this.listeners.get(cameraId)
    if (!set || set.size === 0) return
    const snap = this.snapshot(cameraId)
    for (const cb of set) cb(snap)
  }

  private start(): void {
    this.stopped = false
    void this.consume()
    this.tick = window.setInterval(() => this.onTick(), TICK_MS)
  }

  private stop(): void {
    this.stopped = true
    void this.reader?.cancel().catch(() => {})
    if (this.reconnectTimer !== undefined) clearTimeout(this.reconnectTimer)
    if (this.tick !== undefined) clearInterval(this.tick)
    this.reader = undefined
    this.tick = undefined
    this.reconnectTimer = undefined
    this.cameras.clear()
  }

  // Prune expired boxes; only re-notify a camera whose boxes actually changed or are fading, so a
  // camera holding steady (opacity == 1) boxes doesn't trigger needless re-renders every tick.
  private onTick(): void {
    const now = performance.now()
    for (const [cameraId, m] of this.cameras) {
      let changed = false
      let fading = false
      for (const [k, d] of m) {
        const age = now - d.receivedAt
        const ttl = ttlFor(d.source)
        if (age > ttl) {
          m.delete(k)
          changed = true
        } else if (age > ttl - FADE_MS) {
          fading = true
        }
      }
      if (m.size === 0) this.cameras.delete(cameraId)
      if (changed || fading) this.notify(cameraId)
    }
  }

  private ingest(dto: AnalyticsActivityEntryDto): void {
    let m = this.cameras.get(dto.cameraId)
    if (!m) {
      m = new Map()
      this.cameras.set(dto.cameraId, m)
    }
    m.set(keyOf(dto), { ...dto, receivedAt: performance.now() })
    this.notify(dto.cameraId)
  }

  // Parse whatever full SSE events are in `buffer`, returning the unconsumed tail. Tolerates both
  // \n\n and \r\n\r\n separators and multi-line data: fields (joined with \n).
  private parse(buffer: string): string {
    for (;;) {
      const m = /\r?\n\r?\n/.exec(buffer)
      if (!m) break
      const rawEvent = buffer.slice(0, m.index)
      buffer = buffer.slice(m.index + m[0].length)
      const data = rawEvent
        .split(/\r?\n/)
        .filter((l) => l.startsWith('data:'))
        .map((l) => l.slice(5).trim())
        .join('\n')
      if (!data) continue // comment / keep-alive line (": connected", ":")
      try {
        this.ingest(JSON.parse(data) as AnalyticsActivityEntryDto)
      } catch {
        // ignore a malformed frame, keep the stream alive
      }
    }
    return buffer
  }

  private async consume(): Promise<void> {
    try {
      const resp = await fetch('/api/analytics/live', { headers: { Accept: 'text/event-stream' } })
      if (this.stopped) {
        void resp.body?.cancel()
        return
      }
      if (!resp.ok || !resp.body) throw new Error(`live detections stream failed: ${resp.status}`)

      this.reader = resp.body.getReader()
      const decoder = new TextDecoder()
      let buffer = ''
      for (;;) {
        const { value, done } = await this.reader.read()
        if (done) break
        buffer += decoder.decode(value, { stream: true })
        buffer = this.parse(buffer)
      }
    } catch {
      // network drop / server restart — fall through to reconnect below
    } finally {
      this.reader = undefined
    }

    if (!this.stopped) {
      this.reconnectTimer = window.setTimeout(() => {
        if (!this.stopped) void this.consume()
      }, RECONNECT_MS)
    }
  }
}

export const liveDetectionsClient = new LiveDetectionsClient()
