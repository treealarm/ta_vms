import { useEffect, useRef } from 'react'
import type { MapWatchMessage } from '../dto/mapDtos'
import { mapLiveClient } from './mapLiveClient'

/**
 * Follows one watch scope (`url` from `mapLiveUrl`) and calls `onMessage` for every message.
 * Callbacks are held in a ref so a handler rebuilt on each render does not reopen the stream;
 * a changed url is a new scope and therefore a new connection.
 *
 * `onReconnect` runs when the stream comes back after a break: the messages sent while it was
 * down are gone, so the subscriber re-reads what it shows.
 */
export function useMapLive(
  url: string | null,
  onMessage: (message: MapWatchMessage) => void,
  onReconnect?: () => void,
): void {
  const handler = useRef(onMessage)
  const resumed = useRef(onReconnect)

  useEffect(() => {
    handler.current = onMessage
  }, [onMessage])

  useEffect(() => {
    resumed.current = onReconnect
  }, [onReconnect])

  useEffect(() => {
    if (!url) return
    return mapLiveClient.subscribe(
      url,
      (message) => handler.current(message),
      () => resumed.current?.(),
    )
  }, [url])
}
