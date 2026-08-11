import { useCallback, useSyncExternalStore } from 'react'
import { apiFetch } from '../api/apiFetch'

// How long an answer is trusted before the camera is asked about again. Well short of VmsOnvif's
// own cache TTL, so a change made in the admin Metadata tab (which invalidates that cache) shows up
// on the monitor within a refresh or two rather than minutes later.
const CACHE_MS = 60_000

// Requests are coalesced across cards: a 16-slot grid mounts sixteen overlays within a few frames,
// and one batched call is the difference between one request and sixteen.
const BATCH_MS = 120

// Cameras still being probed on the server are absent from the response rather than reported
// false. Asking again after this picks up the probe's result, which is why a miss is not cached.
const RETRY_MS = 4_000

// How many times a camera may come back missing before we stop asking. A probe that never lands
// means the node cannot answer at all — LiveController swallows that as an absent entry — and
// retrying on a timer would then poll for as long as the page stays open. Giving up leaves the
// button dark, which is the right answer for a camera nobody can reach; a remount asks again.
const MAX_MISSES = 3

type Entry = { capable: boolean; at: number }

const cache = new Map<string, Entry>()
const misses = new Map<string, number>()
const listeners = new Map<string, Set<() => void>>()
let pending = new Set<string>()
let timer: number | undefined

function notify(cameraId: string) {
  for (const cb of listeners.get(cameraId) ?? []) cb()
}

async function flush() {
  timer = undefined
  const batch = [...pending]
  pending = new Set()
  if (batch.length === 0) return

  try {
    const result = await apiFetch<Record<string, boolean>>('/api/live/onvif-bbox-capable', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ cameraIds: batch }),
    })
    const now = Date.now()
    for (const cameraId of batch) {
      const capable = result?.[cameraId]
      if (capable === undefined) {
        // Not probed on the server yet — ask again shortly instead of caching a "no", but only
        // while somebody is still listening and only a few times.
        const seen = (misses.get(cameraId) ?? 0) + 1
        misses.set(cameraId, seen)
        if (seen <= MAX_MISSES && (listeners.get(cameraId)?.size ?? 0) > 0)
          window.setTimeout(() => request(cameraId), RETRY_MS)
        continue
      }
      misses.delete(cameraId)
      cache.set(cameraId, { capable, at: now })
      notify(cameraId)
    }
  } catch {
    // Nothing cached on failure: the button stays dark and the next mount tries again.
  }
}

function request(cameraId: string) {
  pending.add(cameraId)
  if (timer === undefined) timer = window.setTimeout(() => void flush(), BATCH_MS)
}

/**
 * Whether this camera is currently configured to stream its own object boxes, so the monitor knows
 * whether the "show boxes" button has anything to show. Answers false until proven otherwise —
 * a button that appears a moment late is better than one that appears and then vanishes.
 *
 * Built on useSyncExternalStore because the answers really do live in an external store: one
 * module-level cache shared by every card, filled by batched requests rather than per-component
 * fetches. The snapshot deliberately ignores the TTL — a value that expired on its own would flip
 * the button off mid-session; staleness instead triggers a refresh on the next subscribe.
 */
export function useOnvifBboxCapable(cameraId: string | undefined, enabled: boolean): boolean {
  const subscribe = useCallback((onChange: () => void) => {
    if (!cameraId || !enabled) return () => {}

    let set = listeners.get(cameraId)
    if (!set) listeners.set(cameraId, set = new Set())
    set.add(onChange)

    // A fresh subscriber is a fresh chance: clear the give-up counter so a node that has come back
    // is asked again.
    const cached = cache.get(cameraId)
    if (!cached || Date.now() - cached.at >= CACHE_MS) {
      misses.delete(cameraId)
      request(cameraId)
    }

    return () => {
      set.delete(onChange)
      if (set.size === 0) listeners.delete(cameraId)
    }
  }, [cameraId, enabled])

  const getSnapshot = useCallback(
    () => (cameraId && enabled ? cache.get(cameraId)?.capable ?? false : false),
    [cameraId, enabled])

  return useSyncExternalStore(subscribe, getSnapshot)
}
