import { useEffect, useState } from 'react'
import { liveDetectionsClient, type LiveDetection } from './liveDetectionsClient'

/**
 * Returns the currently-visible analytics detections (with fade opacity) for one camera, sourced
 * from the single shared /api/analytics/live stream (see liveDetectionsClient). Subscribing opens
 * that stream if it isn't already open and closes it when the last overlay unsubscribes, so it
 * costs nothing when no overlay is on. Re-subscribes (and clears stale boxes) when cameraId changes.
 */
export function useLiveDetections(cameraId: string): LiveDetection[] {
  const [detections, setDetections] = useState<LiveDetection[]>([])

  useEffect(() => {
    // subscribe() synchronously delivers the new camera's current snapshot, which replaces any
    // boxes left over from a previous cameraId — so no separate reset is needed here.
    return liveDetectionsClient.subscribe(cameraId, setDetections)
  }, [cameraId])

  return detections
}
