import { useEffect, useRef, useState } from 'react'
import type { AnalyticsActivityEntryDto } from '../dto/dtos'

/** A detection ready to draw: the DTO plus a fade multiplier in [0,1] recomputed as it ages out. */
export type LiveDetection = AnalyticsActivityEntryDto & { opacity: number }

/** Internal store entry: the raw detection plus the local clock time it arrived. */
type StoredDetection = AnalyticsActivityEntryDto & { receivedAt: number }

// How long a detection stays on screen after its last update, and how long it fades before that.
// Analytics samples below real FPS, so a box legitimately survives a few missed frames — but a
// stuck box is worthless, so it's dropped shortly after updates stop.
const TTL_MS = 1500
const FADE_MS = 500
// Re-render cadence for smooth fade + pruning between SSE messages.
const TICK_MS = 120
// Backoff before reconnecting a dropped stream.
const RECONNECT_MS = 2000

// A detection's identity is (track, kind): a person and the face on the same track share trackId
// but are distinct boxes.
const keyOf = (d: { trackId: number; kind: string }) => `${d.trackId}:${d.kind}`

function opacityFor(receivedAt: number, now: number): number {
  const age = now - receivedAt
  if (age <= TTL_MS - FADE_MS) return 1
  if (age >= TTL_MS) return 0
  return (TTL_MS - age) / FADE_MS
}

/**
 * Opens an SSE stream of live detections for one camera (via fetch-streaming so the existing Bearer
 * wrapper applies) and returns the currently-visible detections with fade opacity, expiring stale
 * ones. Disabled → nothing is opened, zero cost. Reconnects automatically on drop.
 */
export function useLiveDetections(cameraId: string, enabled: boolean): LiveDetection[] {
  const [detections, setDetections] = useState<LiveDetection[]>([])
  const mapRef = useRef<Map<string, StoredDetection>>(new Map())

  useEffect(() => {
    if (!enabled) {
      mapRef.current.clear()
      setDetections([])
      return
    }

    const map = mapRef.current
    let stopped = false
    let reconnectTimer: number | undefined
    // Stopping the stream via reader.cancel() (not AbortController.abort()) is deliberate: aborting
    // a fetch mid-read rejects the pending read() with an AbortError ("BodyStreamBuffer was
    // aborted"), which the debugger pauses on and looks like a crash. cancel() instead resolves the
    // pending read() as done — no exception thrown on teardown or reconnect.
    let reader: ReadableStreamDefaultReader<Uint8Array> | undefined

    // Snapshot the live set with fresh fade opacity. Called off the render path (SSE callback /
    // interval tick), so reading the clock here is fine.
    const publish = () => {
      const now = performance.now()
      const out: LiveDetection[] = []
      for (const d of map.values()) {
        const opacity = opacityFor(d.receivedAt, now)
        if (opacity > 0) out.push({ ...d, opacity })
      }
      setDetections(out)
    }

    const consume = async () => {
      try {
        const resp = await fetch(`/api/analytics/live/${encodeURIComponent(cameraId)}`, {
          headers: { Accept: 'text/event-stream' },
        })
        if (stopped) {
          void resp.body?.cancel()
          return
        }
        if (!resp.ok || !resp.body) throw new Error(`live detections stream failed: ${resp.status}`)

        reader = resp.body.getReader()
        const decoder = new TextDecoder()
        let buffer = ''

        for (;;) {
          const { value, done } = await reader.read()
          if (done) break
          buffer += decoder.decode(value, { stream: true })

          // SSE events are separated by a blank line; a single event may carry several `data:` lines.
          let sep: number
          while ((sep = buffer.indexOf('\n\n')) !== -1) {
            const rawEvent = buffer.slice(0, sep)
            buffer = buffer.slice(sep + 2)
            const data = rawEvent
              .split('\n')
              .filter((l) => l.startsWith('data:'))
              .map((l) => l.slice(5).trim())
              .join('')
            if (!data) continue // comment/keep-alive line (e.g. ": connected")
            try {
              const dto = JSON.parse(data) as AnalyticsActivityEntryDto
              map.set(keyOf(dto), { ...dto, receivedAt: performance.now() })
              publish()
            } catch {
              // ignore a malformed frame, keep the stream alive
            }
          }
        }
      } catch {
        // Network drop / server restart — fall through to reconnect below.
      } finally {
        reader = undefined
      }

      if (!stopped) {
        reconnectTimer = window.setTimeout(() => {
          if (!stopped) void consume()
        }, RECONNECT_MS)
      }
    }

    void consume()

    // Prune expired boxes and re-render so fade stays smooth between messages.
    const tick = window.setInterval(() => {
      const now = performance.now()
      let changed = false
      for (const [k, d] of map) {
        if (now - d.receivedAt > TTL_MS) {
          map.delete(k)
          changed = true
        }
      }
      // Re-render while boxes are present so opacity keeps updating during the fade window.
      if (changed || map.size > 0) publish()
    }, TICK_MS)

    return () => {
      stopped = true
      // cancel() resolves the pending read() as done (no thrown AbortError); a fetch still awaiting
      // headers has no reader yet and is dropped by the `stopped` guard right after it resolves.
      void reader?.cancel().catch(() => {})
      if (reconnectTimer !== undefined) clearTimeout(reconnectTimer)
      clearInterval(tick)
      map.clear()
    }
  }, [cameraId, enabled])

  return detections
}
