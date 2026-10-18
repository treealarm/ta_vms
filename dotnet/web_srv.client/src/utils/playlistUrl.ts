// Playlist URLs for wall playback.
//
// The window is deliberately narrow and anchored to the committed seek, not to the timeline's
// visible window. Two reasons:
//  - VmsFs answers a wide from..to with `ORDER BY start_ts DESC LIMIT n`, so a broad window
//    silently drops its OLDEST clips — exactly the ones playback is about to need;
//  - the URL carries four short params, so no amount of zooming can grow it into an HTTP 431.
// The server resolves which clips fall inside; the client never enumerates paths.

export const PLAYLIST_LEAD_MS = 60_000
export const PLAYLIST_TRAIL_MS = 600_000

export type WallPlaylistArgs = {
  cameraId: string
  streamName?: string
  seekMs: number
  leadMs?: number
  trailMs?: number
}

export function buildWallPlaylistUrl({
  cameraId,
  streamName,
  seekMs,
  leadMs = PLAYLIST_LEAD_MS,
  trailMs = PLAYLIST_TRAIL_MS,
}: WallPlaylistArgs): string | null {
  if (!cameraId || !Number.isFinite(seekMs)) return null

  const q = new URLSearchParams({
    cameraId,
    from: new Date(seekMs - leadMs).toISOString(),
    to: new Date(seekMs + trailMs).toISOString(),
    // `from` is lead-in so PDT mapping can look backward. START must be the playhead, or hls.js
    // (and its bandwidth probe) load a minute of the wrong clip before the client can seek.
    start: new Date(seekMs).toISOString(),
  })
  if (streamName) q.set('streamName', streamName)

  return `/api/arch/hls/preview.m3u8?${q.toString()}`
}

/** Wall span the URL above asks for — used to decide whether a rebuild is needed at all. */
export function wallPlaylistRequestBounds(
  seekMs: number,
  leadMs = PLAYLIST_LEAD_MS,
  trailMs = PLAYLIST_TRAIL_MS
): { startMs: number; endMs: number } {
  return { startMs: seekMs - leadMs, endMs: seekMs + trailMs }
}
