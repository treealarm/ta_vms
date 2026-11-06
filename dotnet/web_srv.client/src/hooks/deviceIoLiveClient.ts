/**
 * One part's live state as VmsOnvif reports it over Device/Trigger events.
 *
 * `logicalState` is the camera's own value: a relay's coil (active/inactive) or an input's logical
 * true/false. `contact` is the input's circuit state derived from it and the idle state the camera
 * configured; it is absent when that idle state is unknown.
 */
export type DeviceIoUpdate = {
  sourceId: string
  partKind: 'relay' | 'digital_input' | string
  token: string
  logicalState: boolean
  contact?: string | null
  initialized: boolean
  observedAt?: string
}

type Listener = (update: DeviceIoUpdate) => void

const RECONNECT_MS = 2000

// One process-wide SSE connection to /api/DeviceIo/live carries every camera's device states; each
// tile subscribes to its own slice. A connection per tile would exhaust the browser's ~6 HTTP/1.1
// slots on a wall with several cameras and stall live video, which is why the analytics overlay
// keeps one stream too. Opened lazily by the first subscriber and closed when the last leaves.
class DeviceIoLiveClient {
  private listeners = new Map<string, Set<Listener>>()
  private refCount = 0
  private stopped = true
  private reconnectTimer?: number
  // Aborting fetch closes the request even before its promise resolves; the epoch makes a loop
  // already past its await notice that it is stale and let go. See liveDetectionsClient.
  private abort?: AbortController
  private epoch = 0

  subscribe(cameraId: string, listener: Listener): () => void {
    let set = this.listeners.get(cameraId)
    if (!set) this.listeners.set(cameraId, (set = new Set()))
    set.add(listener)
    this.refCount += 1
    if (this.refCount === 1) this.start()

    return () => {
      const listeners = this.listeners.get(cameraId)
      listeners?.delete(listener)
      if (listeners?.size === 0) this.listeners.delete(cameraId)
      this.refCount -= 1
      if (this.refCount === 0) this.stop()
    }
  }

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
    this.epoch += 1
    this.abort?.abort()
    this.abort = undefined
    if (this.reconnectTimer !== undefined) clearTimeout(this.reconnectTimer)
    this.reconnectTimer = undefined
  }

  // Nothing draws this camera, so its update is dropped before it costs a render.
  private ingest(update: DeviceIoUpdate): void {
    const listeners = this.listeners.get(update.sourceId)
    if (!listeners) return
    for (const listener of listeners) listener(update)
  }

  // Parses whatever full SSE events `buffer` holds, returning the unconsumed tail. Tolerates \n\n
  // and \r\n\r\n and multi-line data.
  private parse(buffer: string): string {
    for (;;) {
      const match = /\r?\n\r?\n/.exec(buffer)
      if (!match) break
      const rawEvent = buffer.slice(0, match.index)
      buffer = buffer.slice(match.index + match[0].length)
      const data = rawEvent
        .split(/\r?\n/)
        .filter((line) => line.startsWith('data:'))
        .map((line) => line.slice(5).trim())
        .join('\n')
      if (!data) continue // a comment or keep-alive
      try {
        this.ingest(JSON.parse(data) as DeviceIoUpdate)
      } catch {
        // A malformed frame must not kill the stream.
      }
    }
    return buffer
  }

  private async consume(epoch: number, signal: AbortSignal): Promise<void> {
    const stale = () => this.stopped || epoch !== this.epoch

    try {
      const response = await fetch('/api/DeviceIo/live', {
        headers: { Accept: 'text/event-stream' },
        signal,
      })
      if (stale()) {
        void response.body?.cancel().catch(() => {})
        return
      }
      if (!response.ok || !response.body) throw new Error(`device I/O stream failed: ${response.status}`)

      const reader = response.body.getReader()
      // Aborting rejects both the pending read and reader.closed; the latter is otherwise unobserved.
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
      // Network drop, server restart or our own abort: fall through to the reconnect decision.
    }

    if (stale()) return
    this.reconnectTimer = window.setTimeout(() => {
      if (!stale()) void this.consume(epoch, signal)
    }, RECONNECT_MS)
  }
}

export const deviceIoLiveClient = new DeviceIoLiveClient()
