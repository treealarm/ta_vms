import { useEffect, useState } from 'react'
import { liveEventsClient, type LiveCameraEvent } from './liveEventsClient'

/**
 * Currently-showing ONVIF events (with fade opacity) for one camera, from the single shared
 * /api/analytics/live-events stream. Mirrors useLiveDetections: subscribing opens the stream if it
 * is not already open and closes it when the last consumer leaves, so it costs nothing when the
 * overlay is off.
 */
export function useLiveEvents(cameraId: string, enabled: boolean): LiveCameraEvent[] {
  const [events, setEvents] = useState<LiveCameraEvent[]>([])

  useEffect(() => {
    if (!enabled) return
    // subscribe() synchronously delivers the camera's current snapshot, which replaces anything
    // left over from a previous cameraId.
    return liveEventsClient.subscribe(cameraId, setEvents)
  }, [cameraId, enabled])

  return enabled ? events : []
}
