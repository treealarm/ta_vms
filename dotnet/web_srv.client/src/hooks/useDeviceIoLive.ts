import { useCallback, useEffect, useRef, useState } from 'react'
import { apiFetch } from '../api/apiFetch'
import type { DigitalInputDto, RelayOutputDto } from '../dto/dtos'
import { deviceIoLiveClient } from './deviceIoLiveClient'

export type DeviceIoLiveState = {
  relays: RelayOutputDto[]
  inputs: DigitalInputDto[]
  /** True once the first answer for this camera has arrived — success or failure. */
  loaded: boolean
  /** Why the last read failed; the lists keep their last known values. Null while all is well. */
  error: string | null
}

const EMPTY: DeviceIoLiveState = { relays: [], inputs: [], loaded: false, error: null }

/** Carries the camera the snapshot belongs to, so a switch can never show the previous one's. */
type Snapshot = DeviceIoLiveState & { cameraId: string }

const liveKey = (partKind: string, token: string) => `${partKind}:${token}`

/**
 * Applies the live values heard so far to a fresh read. The read carries the camera's cached state
 * at the moment it was served; an update that arrived while it was in flight is newer, so the
 * buffered value wins.
 */
function withLive(
  relays: RelayOutputDto[],
  inputs: DigitalInputDto[],
  live: Map<string, boolean>,
): { relays: RelayOutputDto[]; inputs: DigitalInputDto[] } {
  const relay = (r: RelayOutputDto) => {
    const value = live.get(liveKey('relay', r.token))
    return value === undefined ? r : { ...r, liveActive: value }
  }
  const input = (i: DigitalInputDto) => {
    const value = live.get(liveKey('digital_input', i.token))
    return value === undefined ? i : { ...i, liveActive: value }
  }
  return { relays: relays.map(relay), inputs: inputs.map(input) }
}

/**
 * The relay and input lists of one camera, read once, and their live states pushed from
 * /api/DeviceIo/live. The camera is not polled: VmsOnvif keeps the state from the camera's
 * Device/Trigger events, and one deployment-wide stream carries every camera's changes to this
 * page (see deviceIoLiveClient).
 *
 * The lists themselves are configuration — mode, delay, idle state — so they are read when the
 * caller mounts and after a settings write, not on a timer.
 */
export function useDeviceIoLive(
  cameraId: string, enabled = true,
): DeviceIoLiveState & { reload: () => void } {
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null)
  // Bumped by reload(): a settings write changes the configuration in the list, which no event
  // carries, so the caller asks for the list again rather than waiting for a timer that is gone.
  const [version, setVersion] = useState(0)
  const reload = useCallback(() => setVersion((v) => v + 1), [])
  // Live values heard while the read is in flight, and after it, keyed by kind:token.
  const live = useRef(new Map<string, boolean>())

  useEffect(() => {
    // The snapshot in state may belong to the previous camera; the return guard below hides it
    // until this read answers, so there is nothing to reset here but the buffered live values.
    live.current = new Map()
    if (!enabled) return

    let cancelled = false
    const load = async () => {
      // Independent lists: a failure reading one must not blank the other, and a camera that
      // answers one and refuses the other should still show what it does have.
      const [r, i] = await Promise.allSettled([
        apiFetch<RelayOutputDto[]>(`/api/DeviceIo/cameras/${encodeURIComponent(cameraId)}/relay-outputs`),
        apiFetch<DigitalInputDto[]>(`/api/DeviceIo/cameras/${encodeURIComponent(cameraId)}/digital-inputs`),
      ])
      if (cancelled) return

      const failures = [r, i]
        .filter((x): x is PromiseRejectedResult => x.status === 'rejected')
        .map((x) => (x.reason instanceof Error ? x.reason.message : String(x.reason)))
        .filter((message) => message)

      setSnapshot((prev) => {
        const known = prev?.cameraId === cameraId ? prev : EMPTY
        const lists = withLive(
          r.status === 'fulfilled' ? r.value : known.relays,
          i.status === 'fulfilled' ? i.value : known.inputs,
          live.current,
        )
        return {
          cameraId,
          ...lists,
          loaded: true,
          error: failures.length > 0 ? [...new Set(failures)].join('; ') : null,
        }
      })
    }

    void load()
    return () => { cancelled = true }
  }, [cameraId, enabled, version])

  useEffect(() => {
    if (!enabled) return
    // The callback closes over the camera this subscription was made for; a change of camera
    // unsubscribes it, so there is no stale-camera case to guard against.
    return deviceIoLiveClient.subscribe(cameraId, (update) => {
      live.current.set(liveKey(update.partKind, update.token), update.logicalState)
      setSnapshot((prev) => {
        if (prev?.cameraId !== cameraId) return prev
        return { ...prev, ...withLive(prev.relays, prev.inputs, live.current) }
      })
    })
  }, [cameraId, enabled])

  return enabled && snapshot?.cameraId === cameraId
    ? { ...snapshot, reload }
    : { ...EMPTY, reload }
}
