import { useEffect, useState } from 'react'
import { apiFetch } from '../api/apiFetch'
import type { DigitalInputDto, RelayOutputDto } from '../dto/dtos'

/**
 * How often a page showing device state asks VmsOnvif for its cache. The camera is not polled: it
 * pushes Device/Trigger property events, which VmsOnvif keeps, and this interval only sets how
 * fresh the copy on screen is.
 */
export const DEVICE_IO_POLL_INTERVAL_MS = 5000

export type DeviceIoLiveState = {
  relays: RelayOutputDto[]
  inputs: DigitalInputDto[]
  /** True once the first answer for this camera has arrived — success or failure. */
  loaded: boolean
  /** Why the last round failed; the lists keep their last known values. Null while all is well. */
  error: string | null
}

const EMPTY: DeviceIoLiveState = { relays: [], inputs: [], loaded: false, error: null }

/** Carries the camera the snapshot belongs to, so a switch can never show the previous one's. */
type Snapshot = DeviceIoLiveState & { cameraId: string }

/**
 * The relay and input lists of one camera, refreshed on an interval while the caller is mounted.
 * Options are not read here: they only gate the idle-state editors and change when a camera is
 * reconfigured, not every five seconds.
 */
export function useDeviceIoLive(cameraId: string, enabled = true): DeviceIoLiveState {
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null)

  useEffect(() => {
    if (!enabled) return

    let cancelled = false
    let timer: ReturnType<typeof setTimeout> | null = null

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
        .filter((m) => m)

      setSnapshot((prev) => {
        const known = prev?.cameraId === cameraId ? prev : EMPTY
        return {
          cameraId,
          relays: r.status === 'fulfilled' ? r.value : known.relays,
          inputs: i.status === 'fulfilled' ? i.value : known.inputs,
          loaded: true,
          error: failures.length > 0 ? [...new Set(failures)].join('; ') : null,
        }
      })

      // Scheduled after the answer instead of a fixed interval: a slow node must not make requests
      // pile up on top of each other.
      timer = setTimeout(() => void load(), DEVICE_IO_POLL_INTERVAL_MS)
    }

    void load()
    return () => {
      cancelled = true
      if (timer) clearTimeout(timer)
    }
  }, [cameraId, enabled])

  return enabled && snapshot?.cameraId === cameraId ? snapshot : EMPTY
}
