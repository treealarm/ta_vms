import { useCallback, useSyncExternalStore } from 'react'
import { apiFetch } from '../api/apiFetch'
import type { CameraAnalyticsSourcesDto } from '../dto/dtos'

// How long an answer is trusted before the camera is asked about again. Short, because the tab this
// feeds is where the same values are being edited: a stale strip next to the switch that changed it
// is worse than an extra request.
const CACHE_MS = 15_000

// Requests are coalesced across mounts: the strip and the list rows ask within a frame of each
// other, and one batched call is the difference between one request and several.
const BATCH_MS = 80

type Entry = { data: CameraAnalyticsSourcesDto; at: number }

const cache = new Map<string, Entry>()
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
    // Only the cameras actually being rendered: the endpoint answers for all of them in one VmsCfg
    // call, so asking for the whole deployment to draw one camera would be waste, not batching.
    const url = `/api/analytics-sources?cameraIds=${batch.map(encodeURIComponent).join(',')}`
    const answered = await apiFetch<CameraAnalyticsSourcesDto[]>(url)
    const now = Date.now()
    for (const dto of answered ?? []) {
      cache.set(dto.cameraId, { data: dto, at: now })
      notify(dto.cameraId)
    }
    // A camera the server did not mention is gone as far as this is concerned; drop any stale
    // entry rather than leaving the tab describing a camera that has been deleted.
    const seen = new Set((answered ?? []).map((d) => d.cameraId))
    for (const cameraId of batch) {
      if (seen.has(cameraId)) continue
      cache.delete(cameraId)
      notify(cameraId)
    }
  } catch {
    // Nothing cached on failure: the tab renders its placeholder and the next mount tries again.
  }
}

function request(cameraId: string) {
  pending.add(cameraId)
  if (timer === undefined) timer = window.setTimeout(() => void flush(), BATCH_MS)
}

/**
 * Drop what is cached and re-ask. Every write that touches an analytics source has to call this —
 * the strip summarises values that are edited on four different pages, and one that keeps showing
 * the pre-save state is actively misleading.
 */
export function invalidateAnalyticsSources(cameraId?: string) {
  if (cameraId) {
    cache.delete(cameraId)
    if ((listeners.get(cameraId)?.size ?? 0) > 0) request(cameraId)
    else notify(cameraId)
    return
  }
  const known = [...cache.keys()]
  cache.clear()
  for (const id of known) {
    if ((listeners.get(id)?.size ?? 0) > 0) request(id)
    else notify(id)
  }
}

/**
 * Every analytics source configured for one camera. Undefined until the first answer lands — the
 * caller renders a placeholder rather than guessing, because "no CV watch" and "not loaded yet"
 * look identical if you default them to the same thing.
 *
 * Built on useSyncExternalStore for the same reason useOnvifBboxCapable is: the answers really do
 * live in one module-level cache shared by every mount, filled by batched requests. The snapshot
 * ignores the TTL so a value cannot vanish mid-session; staleness triggers a refresh on subscribe.
 */
export function useCameraAnalyticsSources(
  cameraId: string | undefined, enabled = true): CameraAnalyticsSourcesDto | undefined {
  const subscribe = useCallback((onChange: () => void) => {
    if (!cameraId || !enabled) return () => {}

    let set = listeners.get(cameraId)
    if (!set) listeners.set(cameraId, set = new Set())
    set.add(onChange)

    const cached = cache.get(cameraId)
    if (!cached || Date.now() - cached.at >= CACHE_MS) request(cameraId)

    return () => {
      set.delete(onChange)
      if (set.size === 0) listeners.delete(cameraId)
    }
  }, [cameraId, enabled])

  const getSnapshot = useCallback(
    () => (cameraId && enabled ? cache.get(cameraId)?.data : undefined),
    [cameraId, enabled])

  return useSyncExternalStore(subscribe, getSnapshot)
}
