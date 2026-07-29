import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { alpha, Box, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'

import { useAppSelector } from '../../store'
import { selectWall } from '../../store/wallPlaybackSlice'
import { wallClockBus } from '../../hooks/wallClockBus'
import { useHlsPlayer } from '../../hooks/useHlsPlayer'
import { buildWallPlaylistUrl } from '../../utils/playlistUrl'
import { wallMsAtMediaTime } from '../../utils/playlistTime'

type Props = {
  monitorId: string
  slotKey: string
  cameraId: string
  streamName?: string
}

/**
 * One wall cell in playback mode.
 *
 * The playlist is derived from the wall's committed `seekMs` only — never from the moving playhead,
 * which would tear down and rebuild the HLS source several times a second.
 */
export default function ArchiveWallPlayer({ monitorId, slotKey, cameraId, streamName }: Props) {
  const { t } = useTranslation()
  const videoRef = useRef<HTMLVideoElement>(null)
  const wall = useAppSelector(selectWall(monitorId))
  const [isOwner, setIsOwner] = useState(false)

  const url = useMemo(
    () => buildWallPlaylistUrl({ cameraId, streamName, seekMs: wall.seekMs }),
    [cameraId, streamName, wall.seekMs]
  )

  const { error, playlistMap, ready } = useHlsPlayer(videoRef, url, {
    autoPlay: wall.armed,
    playEpoch: wall.playEpoch,
  })

  // A cell becomes a clock candidate only once it actually has playable media, so focusing a camera
  // with no archive cannot stop the rest of the wall.
  const hasMedia = ready && (playlistMap?.fragments.length ?? 0) > 0
  useEffect(() => {
    if (!hasMedia) return
    return wallClockBus.registerCandidate(slotKey)
  }, [hasMedia, slotKey])

  useEffect(() => wallClockBus.subscribeOwner((owner) => setIsOwner(owner === slotKey)), [slotKey])

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

  const overlay = !url
    ? t('wall.noCoverage')
    : error
      ? t(
          error.kind === 'network'
            ? 'wall.errorNetwork'
            : error.kind === 'media'
              ? 'wall.errorMedia'
              : 'wall.errorGeneric'
        )
      : !ready
        ? t('wall.loading')
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
        <video ref={videoRef} playsInline muted />
      </Box>

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
          <Typography sx={{ fontSize: '0.75rem', color: alpha('#fff', 0.55) }}>{overlay}</Typography>
        </Box>
      )}
    </Box>
  )
}
