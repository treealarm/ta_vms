import { useEffect, useRef, useState } from 'react'
import Hls from 'hls.js'

import { authXhrSetup } from '../auth/authFetch'
import type { PlaylistMap } from '../utils/playlistTime'

export type HlsErrorKind = 'notFound' | 'network' | 'media' | 'other'
export type HlsPlayerError = { kind: HlsErrorKind; detail: string }

export type UseHlsPlayerResult = {
  error: HlsPlayerError | null
  /** Fragment→wall-clock mapping of the currently loaded playlist. Null until the level loads. */
  playlistMap: PlaylistMap | null
  /** True on Safari-style native HLS, where fragment PDTs are not exposed to us. */
  nativeHls: boolean
  /** True once the manifest parsed and the element has something to show. */
  ready: boolean
}

// Give up recovering after this many consecutive fatal media errors, so a single unplayable clip
// cannot spin forever.
const MAX_RECOVERIES = 2

/**
 * Attaches hls.js to a <video> and exposes the playlist's PDT mapping.
 *
 * Note hls.js issues its own XHRs and therefore bypasses the global fetch wrapper that adds the
 * bearer token — `xhrSetup` is what keeps segment requests authorized.
 */
type LoadState = {
  /** The URL this state describes. Anything reported for a different URL is stale by construction. */
  url: string | null
  error: HlsPlayerError | null
  playlistMap: PlaylistMap | null
  nativeHls: boolean
  ready: boolean
}

const EMPTY_STATE: LoadState = { url: null, error: null, playlistMap: null, nativeHls: false, ready: false }

export function useHlsPlayer(
  videoRef: React.RefObject<HTMLVideoElement | null>,
  url: string | null,
  opts: { autoPlay: boolean; playEpoch?: number } = { autoPlay: false }
): UseHlsPlayerResult {
  const [state, setState] = useState<LoadState>(EMPTY_STATE)

  // Everything the hook reports is keyed by the URL it came from. Deriving "not loaded yet" here
  // rather than clearing state in an effect means a URL change can never leave the previous
  // playlist's fragments or error visible for a render.
  const current: LoadState = state.url === url ? state : { ...EMPTY_STATE, url }

  // Held in a ref so the attach effect below can read the current value without listing `autoPlay`
  // as a dependency — re-running it would tear down and rebuild the whole HLS pipeline on a pause.
  const autoPlayRef = useRef(opts.autoPlay)
  useEffect(() => {
    autoPlayRef.current = opts.autoPlay
  }, [opts.autoPlay])

  useEffect(() => {
    const video = videoRef.current
    if (!video || !url) return

    const patch = (p: Partial<LoadState>) =>
      setState((prev) => ({ ...(prev.url === url ? prev : { ...EMPTY_STATE, url }), ...p, url }))

    let hls: Hls | null = null
    let recoveries = 0
    let disposed = false

    const tryPlay = () => {
      if (!autoPlayRef.current) return
      void video.play().catch(() => {})
    }

    if (Hls.isSupported()) {
      patch({ nativeHls: false })
      hls = new Hls({ enableWorker: true, lowLatencyMode: false, xhrSetup: authXhrSetup })

      hls.on(Hls.Events.MEDIA_ATTACHED, () => hls?.loadSource(url))

      hls.on(Hls.Events.MANIFEST_PARSED, () => {
        if (disposed) return
        patch({ ready: true })
        tryPlay()
      })

      hls.on(Hls.Events.LEVEL_LOADED, (_evt, data) => {
        if (disposed) return
        const fragments = (data.details?.fragments ?? [])
          .map((f) => ({
            startMediaSec: f.start,
            durationSec: f.duration,
            wallMs: f.programDateTime ?? NaN,
          }))
          .filter((f) => Number.isFinite(f.wallMs) && Number.isFinite(f.startMediaSec))

        // The map is stored with the URL it came from, so a stale map can never be applied to a
        // newly loaded playlist.
        patch({ playlistMap: fragments.length > 0 ? { url, fragments } : null })
      })

      hls.on(Hls.Events.ERROR, (_evt, data) => {
        if (disposed || !data.fatal) return
        const detail = `${data.details ?? data.type}`

        if (data.type === Hls.ErrorTypes.MEDIA_ERROR && recoveries < MAX_RECOVERIES) {
          recoveries += 1
          try {
            hls?.recoverMediaError()
            return
          } catch {
            // fall through to reporting
          }
        }

        // hls.js reports an HTTP 404 as a generic network error, but for us it is not a failure at
        // all: it is the server saying this camera has nothing recorded in the requested window.
        // Showing "network error" there sends the operator chasing a connectivity problem.
        const status = data.response?.code
        const kind: HlsErrorKind =
          status === 404
            ? 'notFound'
            : data.type === Hls.ErrorTypes.NETWORK_ERROR
              ? 'network'
              : data.type === Hls.ErrorTypes.MEDIA_ERROR
                ? 'media'
                : 'other'
        patch({ error: { kind, detail } })
      })

      hls.attachMedia(video)
    } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
      // Native HLS exposes no fragment list, so there is no per-segment PDT to map with. What it
      // does expose is getStartDate(): the playlist's EXT-X-PROGRAM-DATE-TIME anchor. That gives a
      // single whole-playlist fragment — enough for the cell to publish a wall clock and so to be
      // eligible to drive the wall, which it otherwise never could.
      //
      // It is deliberately coarse: one fragment means wall time is interpolated linearly across the
      // playlist, so it drifts by however much recorded time the gaps between clips swallow (the
      // very thing per-fragment PDT exists to avoid — see playlistTime.ts). Followers on this path
      // therefore stay on the coarse branch in decideFollow instead of seeking through this map.
      patch({ nativeHls: true })
      const onLoaded = () => {
        if (disposed) return
        const startedAt = (video as HTMLVideoElement & { getStartDate?: () => Date }).getStartDate?.()
        const anchorMs = startedAt instanceof Date ? startedAt.getTime() : NaN
        patch({
          ready: true,
          playlistMap: Number.isFinite(anchorMs) && Number.isFinite(video.duration) && video.duration > 0
            ? { url, fragments: [{ startMediaSec: 0, durationSec: video.duration, wallMs: anchorMs }] }
            : null,
        })
        tryPlay()
      }
      video.addEventListener('loadedmetadata', onLoaded)
      video.src = url
      video.onerror = () => patch({ error: { kind: 'other', detail: 'native HLS failed' } })

      return () => {
        disposed = true
        video.removeEventListener('loadedmetadata', onLoaded)
        video.onerror = null
        video.removeAttribute('src')
        video.load()
      }
    } else {
      patch({ error: { kind: 'other', detail: 'unsupported' } })
      return
    }

    return () => {
      disposed = true
      hls?.destroy()
      video.removeAttribute('src')
      video.load()
    }
  }, [url, videoRef])

  // Drives the element's play/pause state from the wall's `armed` flag. A new play epoch means
  // "play was (re-)authorized" and re-issues play() even when `url` and `autoPlay` are unchanged.
  // Pausing has to be explicit: dropping autoPlay only stops us from starting playback, it does not
  // stop a video that is already running.
  const canPlay = current.ready
  useEffect(() => {
    const video = videoRef.current
    if (!video || !canPlay) return
    if (opts.autoPlay) void video.play().catch(() => {})
    else if (!video.paused) video.pause()
  }, [opts.autoPlay, opts.playEpoch, canPlay, videoRef])

  return {
    error: current.error,
    playlistMap: current.playlistMap,
    nativeHls: current.nativeHls,
    ready: current.ready,
  }
}
