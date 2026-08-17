import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { alpha, Box, Typography } from '@mui/material'
import VolumeUpIcon from '@mui/icons-material/VolumeUp'
import { useTranslation } from 'react-i18next'

import { useAppSelector } from '../../store'
import { selectWall } from '../../store/wallPlaybackSlice'
import { wallClockBus } from '../../hooks/wallClockBus'
import { useHlsPlayer } from '../../hooks/useHlsPlayer'
import ArchiveBboxOverlay from '../bbox/ArchiveBboxOverlay'
import { useWallFollow } from '../../hooks/useWallFollow'
import { useWallCoverage } from '../../hooks/useWallCoverage'
import { buildWallPlaylistUrl, wallPlaylistRequestBounds } from '../../utils/playlistUrl'
import { mediaTimeAtOrAfterWallMs, wallMsAtMediaTime } from '../../utils/playlistTime'

// Resolution of the shared coverage query; must match what WallTimeline asks for so both hit the
// same cache entry.
const COVERAGE_PSEUDO_WIDTH = 1200

type Props = {
  monitorId: string
  slotKey: string
  cameraId: string
  streamName?: string
  /** Every camera on the wall, so all cells and the timeline share one coverage query. */
  wallCameraIds: readonly string[]
  /** Timeline window, so the shared query is keyed identically for every consumer. */
  coverageWindowStartMs: number
  coverageWindowWidthMs: number
  /** Whether the operator asked for analytics boxes over this cell. */
  bboxEnabled?: boolean
}

/**
 * One wall cell in playback mode.
 *
 * The playlist is anchored to a wall instant that changes only on a committed seek or an explicit
 * rebuild — never on the moving playhead, which would tear down and recreate the HLS source
 * several times a second.
 */
export default function ArchiveWallPlayer({
  monitorId,
  slotKey,
  cameraId,
  streamName,
  wallCameraIds,
  coverageWindowStartMs,
  coverageWindowWidthMs,
  bboxEnabled = false,
}: Props) {
  const { t } = useTranslation()
  const videoRef = useRef<HTMLVideoElement>(null)
  const wall = useAppSelector(selectWall(monitorId))
  const [isOwner, setIsOwner] = useState(false)

  // Normally the wall's seek, but a follower whose playhead has drifted out of the loaded span
  // re-anchors itself here without disturbing the rest of the wall. The local anchor is tagged with
  // the seek it was derived from, so a new wall seek supersedes it without needing an effect to
  // reset state.
  const [localAnchor, setLocalAnchor] = useState<{ fromSeekMs: number; ms: number } | null>(null)
  const anchorMs = localAnchor?.fromSeekMs === wall.seekMs ? localAnchor.ms : wall.seekMs

  const handleRebuild = useCallback(
    (aroundWallMs: number) => setLocalAnchor({ fromSeekMs: wall.seekMs, ms: aroundWallMs }),
    [wall.seekMs]
  )

  const url = useMemo(
    () => buildWallPlaylistUrl({ cameraId, streamName, seekMs: anchorMs }),
    [cameraId, streamName, anchorMs]
  )

  // What that URL asked the server for. Derived from the same anchor, so it always describes the
  // playlist currently loaded — the anchor is what the URL is built from, and a changed URL blanks
  // the map until the new one parses.
  const requestBounds = useMemo(() => wallPlaylistRequestBounds(anchorMs), [anchorMs])

  const { error, playlistMap, ready, nativeHls } = useHlsPlayer(videoRef, url, {
    autoPlay: wall.armed,
    playEpoch: wall.playEpoch,
  })

  // Deliberately the WALL's camera list and window, not this cell's: an identical query key means
  // the nine cells of a 3x3 wall and the timeline all resolve to a single batched request instead
  // of nine single-camera ones per seek.
  const cameraIds = useMemo(() => [...wallCameraIds], [wallCameraIds])
  const coverage = useWallCoverage(
    cameraIds,
    coverageWindowStartMs,
    coverageWindowWidthMs,
    COVERAGE_PSEUDO_WIDTH
  )
  const segments = useMemo(() => coverage.byCamera[cameraId] ?? [], [coverage.byCamera, cameraId])

  // A cell becomes a clock candidate only once it actually has playable media, so focusing a camera
  // with no archive cannot stop the rest of the wall.
  const hasMedia = ready && (playlistMap?.fragments.length ?? 0) > 0
  useEffect(() => {
    if (!hasMedia) return
    return wallClockBus.registerCandidate(slotKey)
  }, [hasMedia, slotKey])

  useEffect(() => wallClockBus.subscribeOwner((owner) => setIsOwner(owner === slotKey)), [slotKey])

  // Land exactly on the requested instant.
  //
  // The playlist is fetched with a minute of lead-in for context, and the server's #EXT-X-START
  // points at the start of that window — so left alone the player opens up to a minute early,
  // typically at the beginning of whatever clip covers the lead-in. PDT is the only exact mapping
  // we have, so the position is applied here whenever the playlist is (re)built.
  //
  // Keyed on the ANCHOR, not on the committed seek: a rebuild keeps the same wall.seekMs but loads
  // a playlist around a later instant, and a guard that only tracked the seek would decline to
  // reposition — leaving the cell wherever #EXT-X-START dropped it, i.e. up to a minute earlier
  // than it already was. On a short recording that rewind lands before the rebuild threshold, so
  // the cell rebuilds, rewinds, replays the same minute and never reaches the next one.
  const appliedAnchorRef = useRef<number | null>(null)
  useEffect(() => {
    const video = videoRef.current
    if (!video || !playlistMap) return
    if (appliedAnchorRef.current === anchorMs) return

    const target = mediaTimeAtOrAfterWallMs(playlistMap, anchorMs)
    if (target === null) return

    appliedAnchorRef.current = anchorMs
    if (Math.abs(video.currentTime - target) > 0.25) video.currentTime = target
  }, [playlistMap, anchorMs])

  const publish = useCallback(() => {
    if (!isOwner) return
    const video = videoRef.current
    if (!video) return
    const wallMs = wallMsAtMediaTime(playlistMap, video.currentTime)
    if (wallMs !== null) wallClockBus.publish(slotKey, wallMs)
  }, [isOwner, playlistMap, slotKey])

  useEffect(() => {
    const video = videoRef.current
    if (!video) return
    video.addEventListener('timeupdate', publish)
    return () => video.removeEventListener('timeupdate', publish)
  }, [publish])

  // Only the clock owner is ever audible, and only while the operator asked for sound.
  const audible = wall.audioEnabled && isOwner

  const followStatus = useWallFollow({
    videoRef,
    slotKey,
    armed: wall.armed,
    map: playlistMap,
    segments,
    nativeHls,
    requestBounds,
    onRebuild: handleRebuild,
  })

  const overlay = !url
    ? t('wall.noCoverage')
    : error
      ? t(
          error.kind === 'notFound'
            ? 'wall.noRecordingAtTime'
            : error.kind === 'network'
              ? 'wall.errorNetwork'
              : error.kind === 'media'
                ? 'wall.errorMedia'
                : 'wall.errorGeneric'
        )
      : !ready
        ? t('wall.loading')
        : followStatus === 'noCoverage'
          ? t('wall.noRecordingAtTime')
          : followStatus === 'holding'
            ? t('wall.holdingAtRangeEnd')
            : null

  return (
    <Box sx={{ position: 'relative', width: '100%', height: '100%', bgcolor: 'var(--app-video-bg)' }}>
      <Box
        sx={{
          width: '100%',
          height: '100%',
          '& video': { width: '100%', height: '100%', display: 'block', objectFit: 'contain' },
        }}
      >
        <video ref={videoRef} playsInline muted={!audible} />
      </Box>

      <ArchiveBboxOverlay
        videoRef={videoRef}
        cameraId={cameraId}
        streamName={streamName}
        playlistMap={playlistMap}
        enabled={bboxEnabled}
      />

      {/* Which cell you are hearing is otherwise invisible — the clock owner can move between cells
          as focus or media availability changes. */}
      {audible && (
        <Box
          sx={{
            position: 'absolute',
            top: 4,
            left: 4,
            display: 'flex',
            alignItems: 'center',
            gap: 0.4,
            px: 0.6,
            py: 0.2,
            borderRadius: '3px',
            bgcolor: alpha('#000', 0.5),
            pointerEvents: 'none',
          }}
        >
          <VolumeUpIcon sx={{ fontSize: '0.85rem', color: alpha('#fff', 0.85) }} />
          <Typography sx={{ fontSize: '0.6rem', color: alpha('#fff', 0.85) }}>
            {t('wall.audioOn')}
          </Typography>
        </Box>
      )}

      {overlay && (
        <Box
          sx={{
            position: 'absolute',
            inset: 0,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            pointerEvents: 'none',
            px: 2,
            textAlign: 'center',
          }}
        >
          <Typography
            sx={{
              fontSize: '0.75rem',
              color: alpha('#fff', 0.7),
              bgcolor: alpha('#000', 0.45),
              px: 1,
              py: 0.4,
              borderRadius: '3px',
            }}
          >
            {overlay}
          </Typography>
        </Box>
      )}
    </Box>
  )
}
