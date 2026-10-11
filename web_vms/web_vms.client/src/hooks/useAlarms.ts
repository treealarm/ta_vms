import { useEffect, useMemo, useState } from 'react'
import type { AlarmDto, AlarmsDto } from '../dto/dtos'

/** One light request every few seconds while a map is open; web_vms keeps no subscriptions to push from. */
const POLL_MS = 3000

/** How far back an alarm that has ended still shows, in minutes. */
export const RECENT_ALARM_MINUTES = 15

export type CameraAlarmState = 'active' | 'recent'

export type Alarms = {
  /** Newest first: running ones and those that ended within RECENT_ALARM_MINUTES. */
  alarms: AlarmDto[]
  /** Per camera: whether something is alarming there now, or did lately. */
  byCamera: Map<string, CameraAlarmState>
  /** The newest alarm of each camera, running ones preferred. */
  latest: Map<string, AlarmDto>
}

/**
 * The alarms the operator may see, polled while mounted. A failed poll keeps the last answer: a
 * moment without the server is not a moment without alarms.
 */
export function useAlarms(): Alarms {
  const [alarms, setAlarms] = useState<AlarmDto[]>([])

  useEffect(() => {
    let stale = false
    const load = async () => {
      try {
        const response = await fetch(`/api/fs/alarms?sinceMinutes=${RECENT_ALARM_MINUTES}`)
        if (!response.ok) return
        const data = (await response.json()) as AlarmsDto
        if (!stale) setAlarms(data.alarms)
      } catch {
        // Keep what is on screen; the next poll tries again.
      }
    }
    void load()
    const timer = window.setInterval(() => void load(), POLL_MS)
    return () => {
      stale = true
      clearInterval(timer)
    }
  }, [])

  return useMemo(() => {
    const byCamera = new Map<string, CameraAlarmState>()
    const latest = new Map<string, AlarmDto>()
    for (const a of alarms) {
      if (a.active) byCamera.set(a.cameraId, 'active')
      else if (!byCamera.has(a.cameraId)) byCamera.set(a.cameraId, 'recent')
      const known = latest.get(a.cameraId)
      if (!known || (a.active && !known.active)) latest.set(a.cameraId, a)
    }
    return { alarms, byCamera, latest }
  }, [alarms])
}
