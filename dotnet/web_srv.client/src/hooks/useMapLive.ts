import { useEffect, useRef } from 'react'
import type { MapWatchMessage } from '../dto/mapDtos'
import { mapLiveClient } from './mapLiveClient'

/**
 * Follows one watch scope (`url` from `mapLiveUrl`) and calls `onMessage` for every message.
 * Callbacks are held in a ref so a handler rebuilt on each render does not reopen the stream;
 * a changed url is a new scope and therefore a new connection.
 */
export function useMapLive(url: string | null, onMessage: (message: MapWatchMessage) => void): void {
  const handler = useRef(onMessage)

  useEffect(() => {
    handler.current = onMessage
  }, [onMessage])

  useEffect(() => {
    if (!url) return
    return mapLiveClient.subscribe(url, (message) => handler.current(message))
  }, [url])
}
