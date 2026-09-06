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
 * Takes over a self-guiding camera and names the object to follow, or hands it back.
 *
 * This does not arm anything: whether a camera guides itself is configured in its settings, and a
 * camera with no session refuses. The override it sets is temporary — it lapses by itself when the
 * object is finally lost, so an unattended wall goes back to the camera's own priorities.
 *
 * Polls immediately afterwards instead of writing an optimistic entry: the service may refuse, and
 * a highlight that appeared and then vanished would be worse than one that appears a moment late.
 */
/**
 * Switches a camera's self-guidance on or off from the monitor.
 *
 * The same setting the camera's own settings page writes, not a monitor-local mode — so it survives
 * the operator closing the tab and the service restarting, which is the whole point of it living on
 * the camera. The button is only a shortcut to it.
 *
 * The session it asks for is created by the tracking service reconciling against the new
 * configuration, so the state this hook polls catches up a moment later rather than at once.
 */
export async function setPtzSelfGuidance(cameraId: string, enabled: boolean): Promise<void> {
  await apiFetch(`/api/ptz-tracking-policy/${encodeURIComponent(cameraId)}/enabled`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ enabled }),
  })
  await poll()
}

export async function setPtzTarget(
  cameraId: string,
  target?: TrackingTargetDto | null
): Promise<void> {
  await apiFetch(`/api/ptz-tracking/${encodeURIComponent(cameraId)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ target: target ?? null }),
  })
  await poll()
}
