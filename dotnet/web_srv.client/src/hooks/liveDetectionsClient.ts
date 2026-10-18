import type { AnalyticsActivityEntryDto, AnalyticsDetectionSource } from '../dto/dtos'


/**
 * One instant of one producer, assembled from the per-box events that share a detectedAt.
 *
 * Carries both clocks on purpose. `tsMs` is the producer's, and only differences between two
 * frames' tsMs are ever used — to derive how fast a box is moving. `arrivedAt` is this browser's,
 * and it is what the overlay advances that motion over. Keeping the two apart is what lets the
 * overlay draw without ever having to align the two machines' clocks to each other.
 */
export type LiveFrame = {
  tsMs: number
  arrivedAt: number
  boxes: (AnalyticsActivityEntryDto & { objectId?: string | null })[]
}
export type LiveFramesBySource = Partial<Record<AnalyticsDetectionSource, LiveFrame[]>>
type FramesListener = (frames: LiveFramesBySource) => void

const RECONNECT_MS = 2000

/**
 * Frames older than this are dropped; a couple of seconds is all interpolation can reach.
 *
 * Exported because it is also the ceiling on how long a box can be kept on screen: liveBoxes.ts
 * derives each track's lifetime from how often its producer reports it, and no lifetime can outlast
 * the history the derivation is read from.
 */
export const HISTORY_MS = 4000


const EMPTY_FRAMES: LiveFramesBySource = {}

// One process-wide SSE connection to /api/analytics/live carries detections for ALL cameras; each
// camera overlay subscribes to just its slice. This is deliberately a singleton, not a per-camera
// connection: browsers cap HTTP/1.1 connections at ~6 per origin, so one stream per camera would
// exhaust the pool on a large monitor grid and stall unrelated requests (state polls, WHEP offers).
// The connection is opened lazily on the first subscriber and closed when the last leaves, so it
// costs nothing when no overlay is on.
class LiveDetectionsClient {
  /** Per-camera frame history, keyed by producer timestamp, for the interpolating overlay. */
  private frames = new Map<string, LiveFramesBySource>()
  private frameListeners = new Map<string, Set<FramesListener>>()
  private refCount = 0
  private stopped = true
  private reconnectTimer?: number
  // Cancelling the reader was not enough to close a connection: between `fetch` being issued and
  // its promise resolving there is no reader to cancel, so a stop in that window left the request
  // running with nothing tracking it, and the next start opened a second one beside it. Toggling
  // the overlay eight times left forty of these open — measured — which is far past the six
  // connections a browser allows per origin, so the whole page stopped making requests.
  //
  // The signal closes the request whatever stage it is at, and the epoch makes a loop that was
  // already past its own await notice it is stale and let go of what it holds.
  private abort?: AbortController
  private epoch = 0
  // Cameras with new detections since the last repaint, and the frame callback that will drain
  // them. See notifyFrames.
  private pendingNotify = new Set<string>()
  private notifyHandle?: number

  private start(): void {
    this.stopped = false
    this.epoch += 1
    const epoch = this.epoch
    const abort = new AbortController()
    this.abort = abort
    void this.consume(epoch, abort.signal)
  }

  private stop(): void {
    this.stopped = true
    this.epoch += 1 // anything still in flight belongs to a previous epoch from here on
    this.abort?.abort()
    this.abort = undefined
    if (this.reconnectTimer !== undefined) clearTimeout(this.reconnectTimer)
    this.reconnectTimer = undefined
    if (this.notifyHandle !== undefined) cancelAnimationFrame(this.notifyHandle)
    this.notifyHandle = undefined
    this.pendingNotify.clear()
    this.frames.clear()
  }

  /** Frames for a camera, ascending by producer timestamp, per source. */
  framesFor(cameraId: string): LiveFramesBySource {
    return this.frames.get(cameraId) ?? EMPTY_FRAMES
  }

  private ingest(dto: AnalyticsActivityEntryDto): void {
    // Only for cameras something is actually drawing.
    //
    // The stream carries every camera the operator may see — that is what makes one connection for
    // the whole page possible — but it means switching boxes on for a single tile used to start
    // parsing, timestamping and buffering the detections of the entire deployment on the main
    // thread, and holding a frame history for cameras nobody is looking at. On a wall with a chatty
    // producer that is enough main-thread work to make the live video stutter, and the buffers grow
    // for as long as the overlay stays on.
    //
    // The cost of the guard is that a tile switched on while the stream is already running starts
    // empty instead of inheriting the backlog, so its boxes appear with the next report rather than
    // at once. At one report a second that is not worth the rest of this.
    if (!this.frameListeners.has(dto.cameraId)) return
    this.ingestFrame(dto)
    this.notifyFrames(dto.cameraId)
  }

  private ingestFrame(dto: AnalyticsActivityEntryDto): void {
    const tsMs = Date.parse(dto.detectedAt)
    if (!Number.isFinite(tsMs)) return

    let bySource = this.frames.get(dto.cameraId)
    if (!bySource) this.frames.set(dto.cameraId, (bySource = {}))
    const list = (bySource[dto.source] ??= [])

    // Detections arrive one box at a time; the ones sharing an instant are one frame. Events are
    // effectively ordered, so the frame being filled is almost always the last one.
    let frame = list.length > 0 ? list[list.length - 1] : undefined
    if (!frame || frame.tsMs !== tsMs) {
      frame = { tsMs, arrivedAt: performance.now(), boxes: [] }
      // Out-of-order arrival is rare but must not corrupt the ordering the binary search relies on.
      if (list.length > 0 && tsMs < list[list.length - 1].tsMs) {
        const at = list.findIndex((f) => f.tsMs > tsMs)
        list.splice(at < 0 ? list.length : at, 0, frame)
      } else {
        list.push(frame)
      }
    }
    frame.boxes.push(dto)

    const cutoff = tsMs - HISTORY_MS
    while (list.length > 1 && list[0].tsMs < cutoff) list.shift()
  }

  subscribeFrames(cameraId: string, cb: FramesListener): () => void {
    let set = this.frameListeners.get(cameraId)
    if (!set) this.frameListeners.set(cameraId, (set = new Set()))
    set.add(cb)
    this.refCount += 1
    if (this.refCount === 1) this.start()
    return () => {
      const s = this.frameListeners.get(cameraId)
      if (s) {
        s.delete(cb)
        if (s.size === 0) {
          this.frameListeners.delete(cameraId)
          // Nothing draws this camera any more, so its history is dead weight until the connection
          // closes — which on a wall with one overlay left on is never.
          this.frames.delete(cameraId)
        }
      }
      this.refCount -= 1
      if (this.refCount === 0) this.stop()
    }
  }

  /**
   * Tells a camera's overlays that it has new geometry — at most once per repaint.
   *
   * The producers emit one message per detection, not one per video frame, so a moment with a dozen
   * objects in view arrives as a dozen events milliseconds apart. Answering each one separately
   * meant a dozen React renders and a dozen full canvas repaints for a picture that changed once,
   * and the wall pays that for every cell at once. Coalescing to an animation frame collapses the
   * burst into the single repaint it always was, and costs nothing in latency: the browser was not
   * going to paint any sooner than that anyway.
   *
   * A hidden tab gets no animation frames and so no repaints, which is the right answer too — the
   * overlay's own fade tick reads the buffer directly and catches up when the tab comes back.
   */
  private notifyFrames(cameraId: string): void {
    const set = this.frameListeners.get(cameraId)
    if (!set || set.size === 0) return
    this.pendingNotify.add(cameraId)
    if (this.notifyHandle !== undefined) return
    this.notifyHandle = requestAnimationFrame(() => {
      this.notifyHandle = undefined
      const cameras = [...this.pendingNotify]
      this.pendingNotify.clear()
      for (const id of cameras) this.flushFrames(id)
    })
  }

  private flushFrames(cameraId: string): void {
    const set = this.frameListeners.get(cameraId)
    if (!set || set.size === 0) return
    // A new identity so useSyncExternalStore sees the change; the arrays themselves are mutated in
    // place, which is what keeps ingest cheap at several events per second per camera.
    const next = { ...(this.frames.get(cameraId) ?? {}) }
    this.frames.set(cameraId, next)
    for (const cb of set) cb(next)
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

  private async consume(epoch: number, signal: AbortSignal): Promise<void> {
    const stale = () => this.stopped || epoch !== this.epoch

    try {
      const resp = await fetch('/api/analytics/live', {
        headers: { Accept: 'text/event-stream' },
        signal,
      })
      if (stale()) {
        void resp.body?.cancel().catch(() => {})
        return
      }
      if (!resp.ok || !resp.body) throw new Error(`live detections stream failed: ${resp.status}`)

      const reader = resp.body.getReader()
      // Aborting the fetch errors the body stream, and that rejects two promises: the pending
      // read() below, which the catch handles, and the reader's own `closed`, which nothing
      // awaits. A rejection nobody observes is reported by the browser as an uncaught
      // AbortError, so every overlay unmount raised one in the console.
      void reader.closed.catch(() => {})
      const decoder = new TextDecoder()
      let buffer = ''
      for (;;) {
        const { value, done } = await reader.read()
        if (done) break
        if (stale()) {
          void reader.cancel().catch(() => {})
          return
        }
        buffer += decoder.decode(value, { stream: true })
        buffer = this.parse(buffer)
      }
    } catch {
      // network drop, server restart, or our own abort — fall through to the staleness check below
    }

    if (stale()) return
    this.reconnectTimer = window.setTimeout(() => {
      if (!stale()) void this.consume(epoch, signal)
    }, RECONNECT_MS)
  }
}

export const liveDetectionsClient = new LiveDetectionsClient()
