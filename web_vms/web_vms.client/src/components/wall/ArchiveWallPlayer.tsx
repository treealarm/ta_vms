import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { alpha, Box, Typography } from '@mui/material'
import VolumeUpIcon from '@mui/icons-material/VolumeUp'
import { useTranslation } from 'react-i18next'

import { useAppSelector } from '../../store'
import { selectWall } from '../../store/wallPlaybackSlice'
import { wallClockBus } from '../../hooks/wallClockBus'
import { useHlsPlayer } from '../../hooks/useHlsPlayer'
import { useWallFollow } from '../../hooks/useWallFollow'
import { useWallCoverage } from '../../hooks/useWallCoverage'
import { buildWallPlaylistUrl } from '../../utils/playlistUrl'
import { mediaTimeAtOrAfterWallMs, wallMsAtMediaTime } from '../../utils/playlistTime'
import { HOUR_MS } from '../../utils/wallTime'

// Coverage window this cell keeps loaded to answer "do I have anything at the playhead?".
const COVERAGE_SPAN_MS = 6 * HOUR_MS
const COVERAGE_PSEUDO_WIDTH = 600

type Props = {
  monitorId: string
  slotKey: string
  cameraId: string
  streamName?: string
}

/**
 * One wall cell in playback mode.
 *
 * The playlist is anchored to a wall instant that changes only on a committed seek or an explicit
 * rebuild — never on the moving playhead, which would tear down and recreate the HLS source
 * several times a second.
 */
export default function ArchiveWallPlayer({ monitorId, slotKey, cameraId, streamName }: Props) {
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

  const { error, playlistMap, ready, nativeHls } = useHlsPlayer(videoRef, url, {
    autoPlay: wall.armed,
    playEpoch: wall.playEpoch,
  })

  const cameraIds = useMemo(() => [cameraId], [cameraId])
  const coverage = useWallCoverage(
    cameraIds,
    wall.seekMs - COVERAGE_SPAN_MS / 2,
    COVERAGE_SPAN_MS,
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
  // we have, so the seek is applied here once per committed position.
  const appliedSeekRef = useRef<number | null>(null)
  useEffect(() => {
    const video = videoRef.current
    if (!video || !playlistMap) return
    if (appliedSeekRef.current === wall.seekMs) return

    const target = mediaTimeAtOrAfterWallMs(playlistMap, wall.seekMs)
    if (target === null) return

    appliedSeekRef.current = wall.seekMs
    if (Math.abs(video.currentTime - target) > 0.25) video.currentTime = target
  }, [playlistMap, wall.seekMs])

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
