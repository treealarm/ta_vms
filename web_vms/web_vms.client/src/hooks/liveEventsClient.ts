import type { LiveCameraEventDto } from '../dto/dtos'

/** An event ready to show: the DTO plus a fade multiplier recomputed as it ages out. */
export type LiveCameraEvent = LiveCameraEventDto & { opacity: number }

type StoredEvent = LiveCameraEventDto & { shownAt: number }
type Listener = (events: LiveCameraEvent[]) => void

// An event is a state change, not a position: it stays up far longer than a bbox, because the
// operator has to be able to read it. A camera that reports the clearing edge takes its banner
// down immediately; this is only the backstop for cameras that report the start and go quiet.
const TTL_MS = 8000
const FADE_MS = 1000
const TICK_MS = 250
const RECONNECT_MS = 2000

function opacityFor(shownAt: number, now: number): number {
  const age = now - shownAt
  if (age <= TTL_MS - FADE_MS) return 1
  if (age >= TTL_MS) return 0
  return (TTL_MS - age) / FADE_MS
}

// Identity is the topic: a camera reporting Motion and Tamper at once has two things to say, but a
// second Motion is the same condition and replaces the first.
const keyOf = (e: { topic: string }) => e.topic

/**
 * One process-wide SSE connection to /api/analytics/live-events for ALL cameras, mirroring
 * liveDetectionsClient. Separate connection from the detection stream on purpose: a monitor showing
 * only boxes never opens this one, and vice versa.
 */
class LiveEventsClient {
  private cameras = new Map<string, Map<string, StoredEvent>>()
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
    cb(this.snapshot(cameraId))

    return () => {
      set.delete(cb)
      if (set.size === 0) this.listeners.delete(cameraId)
      this.refCount -= 1
      if (this.refCount === 0) this.stop()
    }
  }

  private snapshot(cameraId: string): LiveCameraEvent[] {
    const m = this.cameras.get(cameraId)
    if (!m) return []
    const now = performance.now()
    const out: LiveCameraEvent[] = []
    for (const e of m.values()) {
      const opacity = opacityFor(e.shownAt, now)
      if (opacity > 0) out.push({ ...e, opacity })
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
    this.tick = window.setInterval(() => this.onTick(), TICK_MS)
    void this.run()
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

  private onTick(): void {
    const now = performance.now()
    for (const [cameraId, m] of this.cameras) {
      let changed = false
      let fading = false
      for (const [k, e] of m) {
        const age = now - e.shownAt
        if (age > TTL_MS) {
          m.delete(k)
          changed = true
        } else if (age > TTL_MS - FADE_MS) {
          fading = true
        }
      }
      if (m.size === 0) this.cameras.delete(cameraId)
      if (changed || fading) this.notify(cameraId)
    }
  }

  private ingest(dto: LiveCameraEventDto): void {
    let m = this.cameras.get(dto.cameraId)
    if (!m) {
      m = new Map()
      this.cameras.set(dto.cameraId, m)
    }
    // The clearing edge takes the banner down rather than putting one up.
    if (dto.active) m.set(keyOf(dto), { ...dto, shownAt: performance.now() })
    else m.delete(keyOf(dto))
    this.notify(dto.cameraId)
  }

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
      if (!data) continue // comment / keep-alive
      try {
        this.ingest(JSON.parse(data) as LiveCameraEventDto)
      } catch {
        // A malformed frame must not kill the stream.
      }
    }
    return buffer
  }

  private async run(): Promise<void> {
    while (!this.stopped) {
      try {
        const res = await fetch('/api/analytics/live-events', {
          headers: { Accept: 'text/event-stream' },
        })
        if (!res.ok || !res.body) throw new Error(`live-events ${res.status}`)

        this.reader = res.body.getReader()
        const decoder = new TextDecoder()
        let buffer = ''
        for (;;) {
          const { done, value } = await this.reader.read()
          if (done) break
          buffer += decoder.decode(value, { stream: true })
          buffer = this.parse(buffer)
        }
      } catch {
        // Network hiccup or gateway restart — fall through to the delay and try again.
      }

      if (this.stopped) return
      await new Promise<void>((resolve) => {
        this.reconnectTimer = window.setTimeout(resolve, RECONNECT_MS)
      })
    }
  }
}

export const liveEventsClient = new LiveEventsClient()
