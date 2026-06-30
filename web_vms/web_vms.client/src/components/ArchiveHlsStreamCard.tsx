import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { alpha, Alert, Box, Divider, IconButton, Stack, Tooltip, Typography } from '@mui/material'
import ErrorOutlineIcon from '@mui/icons-material/ErrorOutline'
import Hls from 'hls.js'

import { authXhrSetup } from '../auth/authFetch'
import type { RecordingDto, RecordingRangeDto } from '../dto/dtos'
import RecordingsList, { type RecordingsListHandle } from './RecordingsList'

const W = '#fff'

// netTerrainTheme panel.elevated — keep in sync with theme/netTerrainTheme.ts
const PANEL_BG = { bgcolor: '#434343', backdropFilter: 'blur(6px)' } as const
const BTN = {
  p: '3px',
  color: alpha(W, 0.75),
  borderRadius: '4px',
  '&:hover': { color: W, bgcolor: alpha(W, 0.12) },
} as const

function isWithinRange(iso: string, range: RecordingRangeDto) {
  const first = range.recordings[0]
  const last = range.recordings[range.recordings.length - 1]
  const end = last?.endTime ?? last?.startTime
  if (!first?.startTime || !end) return false
  const t = new Date(iso).getTime()
  return t >= new Date(first.startTime).getTime() && t <= new Date(end).getTime()
}

function buildPlaylistUrl(camId: string, range: RecordingRangeDto | undefined, seekIso?: string | null) {
  const firstClip = range?.recordings[0]
  if (!camId || !firstClip?.clipUid) return null
  const q = new URLSearchParams({
    cameraId: camId,
    startClipUid: firstClip.clipUid,
    count: String(range!.recordings.length),
  })
  if (seekIso && isWithinRange(seekIso, range!)) q.set('from', seekIso)
  return `/api/arch/hls/from-files.m3u8?${q.toString()}`
}

type Props = { cameraId: string }

export default function ArchiveHlsStreamCard({ cameraId }: Props) {
  const videoRef = useRef<HTMLVideoElement>(null)
  const listRef = useRef<RecordingsListHandle>(null)
  const [hlsError, setHlsError] = useState<string | null>(null)
  const [listOpen, setListOpen] = useState(true)
  const [anchorTs, setAnchorTs] = useState(() => new Date().toISOString())
  const [ranges, setRanges] = useState<RecordingRangeDto[]>([])
  const [startIndex, setStartIndex] = useState(0)
  // Ranges are no longer capped to ~1 minute server-side (see ClipStorage.cs), so a single
  // range can now span an entire uninterrupted recording session. Clicking a clip deep inside
  // such a range must seek the player to that exact clip, not just to the range's first clip.
  const [seekTarget, setSeekTarget] = useState<{ iso: string; filePath: string } | null>(null)
  const rangesRef = useRef<RecordingRangeDto[]>([])
  rangesRef.current = ranges

  const onRangesChange = useCallback((r: RecordingRangeDto[]) => {
    setRanges(r)
    setStartIndex(0)
    setSeekTarget(null)
  }, [])

  const handleSelectRecording = (rec: RecordingDto) => {
    const i = ranges.findIndex((x) =>
      x.recordings.some((r) => r.filePath === rec.filePath && r.startTime === rec.startTime)
    )
    if (i < 0) return
    setStartIndex(i)
    setSeekTarget({ iso: rec.startTime, filePath: rec.filePath })
  }

  const playlistUrl = useMemo(
    () => buildPlaylistUrl(cameraId, ranges[startIndex], seekTarget?.iso),
    [cameraId, ranges, startIndex, seekTarget]
  )

  const rangeFilePath = seekTarget?.filePath ?? ranges[startIndex]?.recordings[0]?.filePath

  const currentClipTime = seekTarget?.iso ?? ranges[startIndex]?.recordings[0]?.startTime
  const formattedTime = currentClipTime
    ? new Date(currentClipTime).toLocaleString(undefined, {
        year: 'numeric', month: '2-digit', day: '2-digit',
        hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false,
      })
    : null

  useEffect(() => {
    const video = videoRef.current
    setHlsError(null)
    if (!video || !playlistUrl) return

    let hls: Hls | null = null
    let prefetched = false
    const tryPlay = () => void video.play().catch(() => {})
    const onFatal = (detail: string) => setHlsError(detail)
    const advanceToNextClip = () => {
      setSeekTarget(null)
      setStartIndex((i) => {
        const list = rangesRef.current
        return i < list.length - 1 ? i + 1 : i
      })
    }
    // The next range's manifest is cheap to warm up (server marks it
    // `Cache-Control: private, max-age=60`), so firing one extra GET a few
    // seconds before this range ends lets the browser serve it from cache
    // instead of paying for clip lookup + sidx parsing right as we switch.
    // Best-effort only: on failure the normal lazy load on `ended` still runs.
    const PREFETCH_LEAD_SECONDS = 5
    const onTimeUpdate = () => {
      if (prefetched || !Number.isFinite(video.duration)) return
      if (video.duration - video.currentTime > PREFETCH_LEAD_SECONDS) return
      prefetched = true
      const nextUrl = buildPlaylistUrl(cameraId, rangesRef.current[startIndex + 1])
      if (nextUrl) void fetch(nextUrl).catch(() => {})
    }

    if (Hls.isSupported()) {
      hls = new Hls({ enableWorker: true, lowLatencyMode: false, xhrSetup: authXhrSetup })
      video.addEventListener('canplay', tryPlay, { once: true })
      video.addEventListener('loadeddata', tryPlay, { once: true })
      hls.on(Hls.Events.MEDIA_ATTACHED, () => hls?.loadSource(playlistUrl))
      hls.on(Hls.Events.MANIFEST_PARSED, () => tryPlay())
      hls.on(Hls.Events.ERROR, (_, data) => {
        if (data.fatal) {
          if (data.type === Hls.ErrorTypes.MEDIA_ERROR) {
            try { hls?.recoverMediaError() } catch {}
          }
          onFatal(`${data.type}: ${data.details ?? ''}`)
        }
      })
      hls.attachMedia(video)
    } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
      video.addEventListener('canplay', tryPlay, { once: true })
      video.src = playlistUrl
      video.onerror = () => onFatal('Native HLS failed')
    } else {
      setHlsError('HLS not supported in this browser')
      return
    }

    video.addEventListener('ended', advanceToNextClip)
    video.addEventListener('timeupdate', onTimeUpdate)
    return () => {
      video.removeEventListener('ended', advanceToNextClip)
      video.removeEventListener('timeupdate', onTimeUpdate)
      video.onerror = null
      video.removeEventListener('canplay', tryPlay)
      video.removeEventListener('loadeddata', tryPlay)
      hls?.destroy()
      video.removeAttribute('src')
      video.load()
    }
  }, [playlistUrl, cameraId, startIndex])

  return (
    <Box sx={{ flex: 1, minHeight: 0, display: 'flex', overflow: 'hidden' }}>
      {/* ── Video area ── */}
      <Box sx={{ flex: 1, minWidth: 0, position: 'relative', bgcolor: 'var(--app-video-bg)' }}>
        {hlsError && (
          <Alert
            severity="error"
            onClose={() => setHlsError(null)}
            sx={{ position: 'absolute', top: 4, left: 4, right: listOpen ? 'auto' : 32, zIndex: 2, fontSize: '0.72rem', py: 0.25 }}
          >
            {hlsError}
          </Alert>
        )}

        {!playlistUrl ? (
          <Box
            sx={{
              width: '100%', height: '100%',
              display: 'flex', alignItems: 'center', justifyContent: 'center',
              color: alpha(W, 0.35), fontSize: '0.78rem', textAlign: 'center', px: 2,
            }}
          >
            Open the recordings list →
          </Box>
        ) : (
          <Box sx={{ width: '100%', height: '100%', '& video': { width: '100%', height: '100%', display: 'block', objectFit: 'contain' } }}>
            <video key={playlistUrl} ref={videoRef} controls playsInline muted />
          </Box>
        )}
      </Box>

      {/* ── Right: compact strip ── */}
      {!listOpen && (
        <Stack
          alignItems="center"
          spacing={0.25}
          sx={{ ...PANEL_BG, width: 28, flexShrink: 0, py: 0.5 }}
        >
          {hlsError && (
            <Tooltip title={hlsError} placement="left">
              <ErrorOutlineIcon sx={{ fontSize: '0.85rem', color: 'error.main', my: 0.25 }} />
            </Tooltip>
          )}

          <Box sx={{ flex: 1 }} />

          <Tooltip title={formattedTime ? `Archive · ${formattedTime}` : 'Open recordings'} placement="left">
            <IconButton size="small" onClick={() => setListOpen(true)} sx={BTN}>
              <span style={{ fontSize: '0.85rem', lineHeight: 1 }}>☰</span>
            </IconButton>
          </Tooltip>
        </Stack>
      )}

      {/* ── Right: recordings panel — always mounted (width=0 when hidden) so data prefetches ── */}
      <Stack
        sx={{
          ...PANEL_BG,
          width: listOpen ? 170 : 0,
          flexShrink: 0,
          minHeight: 0,
          overflow: 'hidden',
          transition: 'width 0.15s ease',
          borderLeft: listOpen ? `1px solid ${alpha(W, 0.08)}` : 'none',
        }}
      >
        <Stack direction="row" alignItems="center" sx={{ px: 1, pt: 0.75, pb: 0.5, flexShrink: 0 }}>
          <Typography sx={{ flex: 1, color: W, fontWeight: 600, fontSize: '0.78rem' }}>
            Archive
          </Typography>
          {formattedTime && (
            <Tooltip title={formattedTime} placement="left">
              <Typography sx={{ fontSize: '0.6rem', color: alpha(W, 0.45), mr: 0.75, cursor: 'default', whiteSpace: 'nowrap' }}>
                {new Date(currentClipTime!).toLocaleDateString('sv-SE')}
              </Typography>
            </Tooltip>
          )}
          <IconButton
            size="small"
            onClick={() => setListOpen(false)}
            sx={{ p: '2px', color: alpha(W, 0.45), '&:hover': { color: W } }}
          >
            ✕
          </IconButton>
        </Stack>

        <Divider sx={{ borderColor: alpha(W, 0.1), flexShrink: 0 }} />

        <Box sx={{ flex: 1, minHeight: 0, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
          <RecordingsList
            ref={listRef}
            open={true}
            cameraId={cameraId}
            timestamp={anchorTs}
            filePath={rangeFilePath}
            playbackStartTime={currentClipTime}
            onSelect={handleSelectRecording}
            onTimeChange={setAnchorTs}
            onRangesChange={onRangesChange}
            dark
          />
        </Box>
      </Stack>
    </Box>
  )
}
