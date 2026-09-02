import { useEffect, useState, type RefObject } from 'react'
import { useArchiveMetadata } from '../../hooks/archiveMetadataClient'
import type { PlaylistMap } from '../../utils/playlistTime'
import { wallMsAtMediaTime } from '../../utils/playlistTime'
import BboxCanvas from './BboxCanvas'
import { boxesAt } from './interpolate'
import type { DrawableBox } from './bboxDraw'

interface Props {
  videoRef: RefObject<HTMLVideoElement | null>
  cameraId: string
  streamName?: string
  playlistMap: PlaylistMap | null
  enabled: boolean
  onFindSimilar?: (box: DrawableBox) => void
  /** Appearance the operator is inspecting — gold box, others dimmed. */
  highlightTrackId?: number | null
  highlightKind?: string | null
}

/**
 * Draws recorded analytics geometry over archive playback.
 *
 * The instant it draws for comes from this element's own media time mapped through its own
 * playlist's PROGRAM-DATE-TIME — never from wallClockBus. The bus carries the time of whichever
 * cell currently *drives* the wall; a following cell is showing a slightly different moment and
 * would get somebody else's boxes.
 */
export default function ArchiveBboxOverlay({
  videoRef,
  cameraId,
  streamName,
  playlistMap,
  enabled,
  onFindSimilar,
  highlightTrackId,
  highlightKind,
}: Props) {
  const [wallMs, setWallMs] = useState<number | null>(null)

  useEffect(() => {
    const video = videoRef.current
    if (!video || !enabled || !playlistMap) return

    let raf = 0
    let disposed = false

    const publish = () => {
      const ms = wallMsAtMediaTime(playlistMap, video.currentTime)
      setWallMs((prev) => (prev === ms ? prev : ms))
    }

    // Driven by rAF rather than `timeupdate`, which fires about four times a second — visibly less
    // often than the video moves, so the boxes would stutter behind the picture. The loop stops
    // itself as soon as the element is paused, so an idle wall costs nothing.
    const tick = () => {
      if (disposed) return
      publish()
      raf = video.paused ? 0 : requestAnimationFrame(tick)
    }
    const kick = () => {
      if (raf === 0 && !disposed) raf = requestAnimationFrame(tick)
    }

    video.addEventListener('play', kick)
    video.addEventListener('playing', kick)
    video.addEventListener('pause', kick)
    video.addEventListener('seeked', kick)
    video.addEventListener('loadedmetadata', kick)
    kick()

    return () => {
      disposed = true
      if (raf !== 0) cancelAnimationFrame(raf)
      video.removeEventListener('play', kick)
      video.removeEventListener('playing', kick)
      video.removeEventListener('pause', kick)
      video.removeEventListener('seeked', kick)
      video.removeEventListener('loadedmetadata', kick)
    }
  }, [videoRef, playlistMap, enabled])

  // Derived rather than cleared in the effect: a stale instant from a torn-down playlist must not
  // survive into the next one, and resetting it through state would be a render cascade.
  const activeWallMs = enabled && playlistMap ? wallMs : null

  const frames = useArchiveMetadata(cameraId, streamName ?? '', activeWallMs, enabled)

  if (!enabled) return null

  const boxes = activeWallMs === null ? [] : boxesAt(frames, activeWallMs)
  const marked = highlightTrackId == null || !highlightKind
    ? boxes
    : boxes.map((b) => ({
      ...b,
      highlighted: b.trackId === highlightTrackId && b.kind === highlightKind,
    }))
  return <BboxCanvas videoRef={videoRef} boxes={marked} onFindSimilar={onFindSimilar} />
}
