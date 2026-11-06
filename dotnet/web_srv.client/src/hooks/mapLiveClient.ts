import type { MapWatchMessage } from '../dto/mapDtos'

type Listener = (message: MapWatchMessage) => void
type ReconnectHandler = () => void

const RECONNECT_MS = 2000

type Scope = {
  url: string
  listeners: Set<Listener>
  reconnects: Set<ReconnectHandler>
  abort?: AbortController
  reconnectTimer?: number
  epoch: number
  stopped: boolean
  /** A stream that has connected before: what it missed while down it will never send. */
  connected: boolean
}

/** SSE frames are separated by a blank line; an empty data payload is a comment or heartbeat. */
function parseFrames(buffer: string, onMessage: (message: MapWatchMessage) => void): string {
  for (;;) {
    const match = /\r?\n\r?\n/.exec(buffer)
    if (!match) return buffer

    const frame = buffer.slice(0, match.index)
    buffer = buffer.slice(match.index + match[0].length)

    const data = frame
      .split(/\r?\n/)
      .filter((line) => line.startsWith('data:'))
      .map((line) => line.slice(5).trim())
      .join('\n')

    if (!data) continue

    try {
      onMessage(JSON.parse(data) as MapWatchMessage)
    } catch {
      // A malformed frame must not kill the stream.
    }
  }
}

/**
 * One SSE connection per watched scope. Square's map socket switched scope over a bidirectional
 * socket; the gateway takes the scope as a connection parameter (Dapr proxies server streams, not
 * bidirectional calls), so a different box or id set is a new connection. Several components may
 * follow the same scope — they share it, and it closes when the last one leaves.
 */
class MapLiveClient {
  private scopes = new Map<string, Scope>()

  subscribe(url: string, listener: Listener, onReconnect?: ReconnectHandler): () => void {
    let scope = this.scopes.get(url)
    if (!scope) {
      scope = {
        url,
        listeners: new Set(),
        reconnects: new Set(),
        epoch: 0,
        stopped: false,
        connected: false,
      }
      this.scopes.set(url, scope)
      this.start(scope)
    }

    const current = scope
    current.listeners.add(listener)
    if (onReconnect) current.reconnects.add(onReconnect)

    return () => {
      if (onReconnect) current.reconnects.delete(onReconnect)
      this.unsubscribe(current, listener)
    }
  }

  private unsubscribe(scope: Scope, listener: Listener): void {
    scope.listeners.delete(listener)
    if (scope.listeners.size > 0) return

    this.stop(scope)
    this.scopes.delete(scope.url)
  }

  private start(scope: Scope): void {
    scope.stopped = false
    scope.epoch += 1
    const epoch = scope.epoch
    const abort = new AbortController()
    scope.abort = abort
    void this.run(scope, epoch, abort.signal)
  }

  private stop(scope: Scope): void {
    scope.stopped = true
    scope.epoch += 1
    scope.abort?.abort()
    scope.abort = undefined
    if (scope.reconnectTimer !== undefined) clearTimeout(scope.reconnectTimer)
    scope.reconnectTimer = undefined
  }

  private emit(scope: Scope, message: MapWatchMessage): void {
    for (const listener of scope.listeners) listener(message)
  }

  private async run(scope: Scope, epoch: number, signal: AbortSignal): Promise<void> {
    const stale = () => scope.stopped || epoch !== scope.epoch

    while (!stale()) {
      try {
        const response = await fetch(scope.url, {
          headers: { Accept: 'text/event-stream' },
          signal,
        })

        if (stale()) {
          void response.body?.cancel().catch(() => {})
          return
        }

        if (!response.ok || !response.body) throw new Error(`map live ${response.status}`)

        // The watch diffs against what it has already sent, and a new stream has sent nothing:
        // whatever left its scope while the link was down is never retracted by it. The subscribers
        // are told, so they can re-read instead of keeping a stale page.
        const resumed = scope.connected
        scope.connected = true
        if (resumed) {
          for (const handler of scope.reconnects) handler()
        }

        const reader = response.body.getReader()
        // Aborting the fetch also rejects the reader's own `closed`; nobody awaits it, and an
        // unobserved rejection is reported by the browser as an uncaught AbortError.
        void reader.closed.catch(() => {})

        const decoder = new TextDecoder()
        let buffer = ''
        for (;;) {
          const { done, value } = await reader.read()
          if (done) break
          if (stale()) {
            void reader.cancel().catch(() => {})
            return
          }

          buffer += decoder.decode(value, { stream: true })
          buffer = parseFrames(buffer, (message) => this.emit(scope, message))
        }
      } catch {
        // Network hiccup, gateway restart, or our own abort — the staleness check decides.
      }

      if (stale()) return
      await new Promise<void>((resolve) => {
        scope.reconnectTimer = window.setTimeout(resolve, RECONNECT_MS)
      })
    }
  }
}

export const mapLiveClient = new MapLiveClient()
