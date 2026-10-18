import { useEffect, useMemo, useRef, useState } from 'react'
import { Alert, Box, IconButton, Stack, Tooltip, Typography } from '@mui/material'
import DirectionsRunIcon from '@mui/icons-material/DirectionsRun'
import { useTranslation } from 'react-i18next'

import { useCameraName } from '../store'
import { useHlsPlayer } from '../hooks/useHlsPlayer'
import { buildWallPlaylistUrl, wallPlaylistRequestBounds } from '../utils/playlistUrl'
import { mediaTimeAtOrAfterWallMs, playlistWallBounds } from '../utils/playlistTime'
import { mergeSegments, snapToNearest, type Segment } from '../utils/archiveRanges'
import type { RecordingsResponseDto } from '../dto/dtos'
import ArchiveBboxOverlay from './bbox/ArchiveBboxOverlay'
import { useArchiveMetadataCoverage } from '../hooks/archiveMetadataClient'

type Props = {
  cameraId: string | null
  seekMs: number | null
  streamName?: string
  highlightTrackId?: number | null
  highlightKind?: string | null
}

/** How far a detection may sit in an archive gap before we refuse to jump to nearby footage. */
const GAP_SNAP_MAX_MS = 45 * 60_000

/** Wall playlists carry a minute of lead-in for the monitor wall; here we only need a breath of
 *  context before the detection, otherwise #EXT-X-START opens ~60 s before the object. */
const FORENSIC_LEAD_MS = 2_000
/** Long enough to watch the object leave the frame; the wall's 10-minute trail would make
 *  GetClipIndexes parse every clip in the window before the first frame appears. */
const FORENSIC_TRAIL_MS = 30_000

const ASSUMED_OPEN_CLIP_MS = 60_000

function recordingsToSegments(
  recordings: RecordingsResponseDto['recordings'],
  streamName?: string
): Segment[] {
  const out: Segment[] = []
  for (const rec of recordings) {
    if (streamName && rec.streamName && rec.streamName !== streamName) continue
    const startMs = Date.parse(rec.startTime)
    if (!Number.isFinite(startMs)) continue
    const endParsed = rec.endTime ? Date.parse(rec.endTime) : NaN
    const endMs = Number.isFinite(endParsed) && endParsed > startMs
      ? endParsed
      : startMs + ASSUMED_OPEN_CLIP_MS
    out.push({ startMs, endMs })
  }
  return mergeSegments(out, 0)
}

/**
 * Archive preview for a single camera instant — the forensic findings equivalent of
 * ArchiveHlsPreview, which is tied to a retention event's playlist.
 *
 * Detections can exist without archive (live analytics kept running through a recording hole).
 * When the exact instant has no footage, snap to the nearest clip and say so — do not leave the
 * operator staring at a bare "No recording" next to a valid "Object seen at".
 */
export default function DetectionArchivePreview({
  cameraId, seekMs, streamName, highlightTrackId, highlightKind,
}: Props) {
  const { t } = useTranslation()
  const cameraName = useCameraName(cameraId ?? undefined)
  const videoRef = useRef<HTMLVideoElement>(null)
  const [bboxOpen, setBboxOpen] = useState(true)
  const [playSeekMs, setPlaySeekMs] = useState<number | null>(seekMs)
  const [gapSnapMs, setGapSnapMs] = useState<number | null>(null)
  const [snapFailed, setSnapFailed] = useState(false)

  useEffect(() => {
    setPlaySeekMs(seekMs)
    setGapSnapMs(null)
    setSnapFailed(false)
  }, [cameraId, seekMs, streamName])

  const url = useMemo(
    () => (cameraId && playSeekMs !== null
      ? buildWallPlaylistUrl({
        cameraId, streamName, seekMs: playSeekMs, leadMs: FORENSIC_LEAD_MS, trailMs: FORENSIC_TRAIL_MS,
      })
      : null),
    [cameraId, streamName, playSeekMs]
  )
  // EXT-X-START is the detection instant (see playlistUrl `start=`). Autoplay can run immediately;
  // the PDT seek below only trims leftover lead-in if PRECISE=YES was ignored.
  const { error, playlistMap, ready } = useHlsPlayer(videoRef, url, { autoPlay: true })

  // Exact-time playlist 404 → look up nearby clips and re-anchor playback.
  useEffect(() => {
    if (error?.kind !== 'notFound' || !cameraId || seekMs === null || snapFailed) return
    // Already jumped once; a second 404 means there is nothing usable nearby.
    if (playSeekMs !== seekMs) {
      setSnapFailed(true)
      return
    }

    let cancelled = false
    void (async () => {
      try {
        const response = await fetch('/api/fs/recordings', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            cameraId,
            streamName: streamName || undefined,
            timestamp: new Date(seekMs).toISOString(),
            limit: 40,
          }),
        })
        if (!response.ok || cancelled) {
          if (!cancelled) setSnapFailed(true)
          return
        }
        const body = await response.json() as RecordingsResponseDto
        const segs = recordingsToSegments(body.recordings ?? [], streamName)
        const snapped = snapToNearest(segs, seekMs, GAP_SNAP_MAX_MS)
        if (cancelled) return
        if (snapped === null || snapped === seekMs) {
          setSnapFailed(true)
          return
        }
        setGapSnapMs(snapped)
        setPlaySeekMs(snapped)
      } catch {
        if (!cancelled) setSnapFailed(true)
      }
    })()

    return () => {
      cancelled = true
    }
  }, [error, cameraId, seekMs, playSeekMs, streamName, snapFailed])

  // Land on the detection instant, then play. The playlist still has a small lead-in so PDT
  // mapping works; without this seek the picture is that lead-in, not the object.
  useEffect(() => {
    const video = videoRef.current
    if (!video || !playlistMap || playSeekMs === null || !ready) return
    const target = mediaTimeAtOrAfterWallMs(playlistMap, playSeekMs)
    if (target === null) return

    let cancelled = false
    const play = () => {
      if (!cancelled) void video.play().catch(() => {})
    }
    const apply = () => {
      if (cancelled) return
      if (Math.abs(video.currentTime - target) > 0.15) video.currentTime = target
      else play()
    }
    apply()
    video.addEventListener('seeked', play)
    const retry = window.setTimeout(apply, 200)
    return () => {
      cancelled = true
      video.removeEventListener('seeked', play)
      window.clearTimeout(retry)
    }
  }, [playlistMap, playSeekMs, ready])

  const wallBounds = playlistWallBounds(playlistMap)
    ?? (playSeekMs !== null ? wallPlaylistRequestBounds(playSeekMs, FORENSIC_LEAD_MS, FORENSIC_TRAIL_MS) : null)
  const coverage = useArchiveMetadataCoverage(
    cameraId ?? undefined,
    streamName ?? '',
    wallBounds?.startMs ?? null,
    wallBounds?.endMs ?? null
  )
  const showBboxToggle = coverage === 'some'

  if (!cameraId || seekMs === null) {
    return (
      <Typography variant="body2" color="text.secondary">
        {t('archive.selectHint')}
      </Typography>
    )
  }

  const lookingForNearest = error?.kind === 'notFound' && !snapFailed && playSeekMs === seekMs
  const hlsError = error && error.kind !== 'notFound'
    ? t(
        error.kind === 'network'
          ? 'wall.errorNetwork'
          : error.kind === 'media'
            ? 'wall.errorMedia'
            : 'wall.errorGeneric'
      )
    : error?.kind === 'notFound' && snapFailed
      ? t('forensic.noRecordingAtDetection')
      : null

  return (
    <Stack spacing={1} sx={{ height: '100%', minHeight: 0 }}>
      <Stack direction="row" spacing={1} alignItems="center">
        <Typography variant="subtitle2" sx={{ flex: 1, minWidth: 0 }} noWrap>
          {cameraName} · {new Date(seekMs).toLocaleString()}
        </Typography>
        {showBboxToggle && (
          <Tooltip title={bboxOpen ? t('cameraCard.bboxClose') : t('cameraCard.bboxOpen')}>
            <IconButton
              size="small"
              onClick={() => setBboxOpen(!bboxOpen)}
              color={bboxOpen ? 'primary' : 'default'}
            >
              <DirectionsRunIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        )}
      </Stack>
      {highlightTrackId != null && highlightKind && showBboxToggle && bboxOpen && (
        <Typography variant="caption" color="warning.main">
          {t('forensic.targetBoxHint')}
        </Typography>
      )}

      {gapSnapMs !== null && !hlsError && (
        <Alert severity="warning">
          {t('forensic.archiveGapNearest', { time: new Date(gapSnapMs).toLocaleString() })}
        </Alert>
      )}
      {hlsError && <Alert severity="error">{hlsError}</Alert>}
      {lookingForNearest && (
        <Typography variant="body2" color="text.secondary">{t('forensic.lookingForNearestArchive')}</Typography>
      )}
      {!ready && !hlsError && !lookingForNearest && url && (
        <Typography variant="body2" color="text.secondary">{t('wall.loading')}</Typography>
      )}

      <Box
        sx={{
          position: 'relative',
          flex: 1,
          minHeight: 0,
          width: '100%',
          borderRadius: 1,
          overflow: 'hidden',
          bgcolor: 'var(--app-video-bg)',
          '& video': { width: '100%', height: '100%', display: 'block', objectFit: 'contain' },
        }}
      >
        <video ref={videoRef} controls playsInline muted />
        <ArchiveBboxOverlay
          videoRef={videoRef}
          cameraId={cameraId}
          streamName={streamName}
          playlistMap={playlistMap}
          enabled={bboxOpen && showBboxToggle}
          highlightTrackId={highlightTrackId}
          highlightKind={highlightKind}
        />
      </Box>
    </Stack>
  )
}
