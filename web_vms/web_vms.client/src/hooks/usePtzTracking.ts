import { useCallback, useSyncExternalStore } from 'react'
import { apiFetch } from '../api/apiFetch'
import type { PtzTrackingStateDto, TrackingTargetDto } from '../dto/dtos'

/**
 * Which cameras are following an object right now, shared by every tile on the wall.
 *
 * One poll for the whole page rather than one per tile: the endpoint already answers for every
 * camera the operator can see, and a 16-slot grid asking separately would be sixteen requests every
 * couple of seconds for the same body. Same shape as useOnvifBboxCapable — a module-level store
 * read through useSyncExternalStore — for the same reason.
 *
 * Polled rather than streamed. This is not the object's position, which arrives on the detections
 * stream and moves several times a second; it is only which camera is armed and what it latched
 * onto, and that changes when an operator presses a button.
 */
const POLL_MS = 2000

const states = new Map<string, PtzTrackingStateDto>()
const listeners = new Set<() => void>()
let timer: number | undefined

function notify() {
  for (const cb of listeners) cb()
}

async function poll() {
  try {
    const rows = await apiFetch<PtzTrackingStateDto[]>('/api/ptz-tracking')
    const next = new Map((rows ?? []).map((r) => [r.cameraId, r]))
    if (!sameKeys(next) || !sameValues(next)) {
      states.clear()
      for (const [k, v] of next) states.set(k, v)
      notify()
    }
  } catch {
    // A failed poll is not an answer: the buttons keep showing what was last known rather than
    // flickering off every time a request is lost.
  }
}

function sameKeys(next: Map<string, PtzTrackingStateDto>): boolean {
  if (next.size !== states.size) return false
  for (const key of next.keys()) if (!states.has(key)) return false
  return true
}

function sameValues(next: Map<string, PtzTrackingStateDto>): boolean {
  for (const [key, value] of next) {
    const prev = states.get(key)
    if (!prev) return false
    if (prev.active !== value.active
      || prev.explicitTarget !== value.explicitTarget
      || prev.lastError !== value.lastError
      || prev.target?.trackId !== value.target?.trackId
      || prev.target?.source !== value.target?.source
      || prev.target?.kind !== value.target?.kind) return false
  }
  return true
}

function subscribe(onChange: () => void): () => void {
  listeners.add(onChange)
  if (listeners.size === 1) {
    void poll()
    timer = window.setInterval(() => void poll(), POLL_MS)
  }
  return () => {
    listeners.delete(onChange)
    if (listeners.size > 0) return
    if (timer !== undefined) window.clearInterval(timer)
    timer = undefined
    states.clear()
  }
}

/** The tracking state of one camera, or null when it is not tracking anything. */
export function usePtzTracking(cameraId: string | undefined): PtzTrackingStateDto | null {
  // The map is only rewritten when something actually changed, so an unchanged camera keeps
  // handing back the same object — which is what useSyncExternalStore needs to stop re-rendering.
  const getSnapshot = useCallback(
    () => (cameraId ? states.get(cameraId) ?? null : null),
    [cameraId])
  return useSyncExternalStore(subscribe, getSnapshot)
}

/**
 * Turns tracking on or off, and names the object to follow.
 *
 * Polls immediately afterwards instead of writing an optimistic entry: the service may refuse —
 * a camera not marked as having telemetry, or one no VmsOnvif can reach — and a button that lit up
 * and then went dark again would be worse than one that lights a moment late.
 */
export async function setPtzTracking(
  cameraId: string,
  enabled: boolean,
  target?: TrackingTargetDto | null
): Promise<void> {
  await apiFetch(`/api/ptz-tracking/${encodeURIComponent(cameraId)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ enabled, target: target ?? null }),
  })
  await poll()
}
