import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Chip,
  Divider,
  FormControlLabel,
  MenuItem,
  Stack,
  Switch,
  TextField,
  Typography,
} from '@mui/material'
import DownloadIcon from '@mui/icons-material/Download'
import Hls from 'hls.js'
import { useTranslation } from 'react-i18next'
import { useAppSelector } from '../store'

interface FsFileEventDto {
  clipUid: string
  filePath: string
  cameraId: string
  streamName: string
  retained: boolean
  sizeBytes: number
  occurredAt: string
  eventType: 'ingested' | 'deleted'
  endTs?: string
}

function formatBytes(bytes: number): string {
  if (bytes === 0) return '0 B'
  const k = 1024
  const sizes = ['B', 'KB', 'MB', 'GB']
  const i = Math.floor(Math.log(bytes) / Math.log(k))
  return `${parseFloat((bytes / Math.pow(k, i)).toFixed(1))} ${sizes[i]}`
}

function basename(filePath: string): string {
  return filePath.split(/[\\/]/).pop() ?? filePath
}

function ClipPlayer({ clipUid, cameraId }: { clipUid: string; cameraId: string }) {
  const videoRef = useRef<HTMLVideoElement>(null)
  const [hlsError, setHlsError] = useState<string | null>(null)

  const playlistUrl = `/api/arch/hls/from-files.m3u8?cameraId=${encodeURIComponent(cameraId)}&startClipUid=${encodeURIComponent(clipUid)}&count=1`

  useEffect(() => {
    const video = videoRef.current
    if (!video) return

    setHlsError(null)
    let hls: Hls | null = null

    const tryPlay = () => void video.play().catch(() => {})
    const onFatal = (detail: string) => setHlsError(detail)

    if (Hls.isSupported()) {
      hls = new Hls({ enableWorker: true, lowLatencyMode: false })
      video.addEventListener('canplay', tryPlay, { once: true })
      hls.on(Hls.Events.MEDIA_ATTACHED, () => hls?.loadSource(playlistUrl))
      hls.on(Hls.Events.MANIFEST_PARSED, tryPlay)
      hls.on(Hls.Events.ERROR, (_, data) => {
        if (!data.fatal) return
        if (data.type === Hls.ErrorTypes.MEDIA_ERROR) {
          onFatal(`Media error (${data.details})`)
          try { hls?.recoverMediaError() } catch { /* ignore */ }
        } else {
          onFatal(`Playback error: ${data.type} ${data.details ?? ''}`)
        }
      })
      hls.attachMedia(video)
    } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
      video.addEventListener('canplay', tryPlay, { once: true })
      video.src = playlistUrl
    } else {
      setHlsError('HLS not supported in this browser')
      return
    }

    return () => {
      video.removeEventListener('canplay', tryPlay)
      hls?.destroy()
      video.removeAttribute('src')
      video.load()
    }
  }, [playlistUrl])

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', p: 1, gap: 1 }}>
      {hlsError && <Alert severity="error" onClose={() => setHlsError(null)}>{hlsError}</Alert>}
      <Box sx={{ flex: 1, borderRadius: 1, overflow: 'hidden', bgcolor: 'var(--app-video-bg)', '& video': { width: '100%', height: '100%', display: 'block', objectFit: 'contain' } }}>
        <video
          key={`${clipUid}`}
          ref={videoRef}
          controls
          playsInline
          muted
        />
      </Box>
    </Box>
  )
}

export default function FsActivityPanel() {
  const { t } = useTranslation()
  const camerasMap = useAppSelector((s) => s.live.cameraStatesById)
  const cameraIds = Object.keys(camerasMap).sort()

  const [events, setEvents] = useState<FsFileEventDto[]>([])
  const [selected, setSelected] = useState<FsFileEventDto | null>(null)
  const [cameraFilter, setCameraFilter] = useState('')
  const [textFilter, setTextFilter] = useState('')
  const [autoRefresh, setAutoRefresh] = useState(true)
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null)

  const fetchEvents = useCallback(async () => {
    try {
      const resp = await fetch('/api/fs/activity?limit=200')
      if (!resp.ok) return
      const data = await resp.json() as { events: FsFileEventDto[] }
      setEvents(data.events)
    } catch {
      // silently ignore fetch errors in debug panel
    }
  }, [])

  useEffect(() => { void fetchEvents() }, [fetchEvents])

  useEffect(() => {
    if (autoRefresh) {
      timerRef.current = setInterval(() => void fetchEvents(), 3000)
    } else {
      if (timerRef.current) clearInterval(timerRef.current)
    }
    return () => { if (timerRef.current) clearInterval(timerRef.current) }
  }, [autoRefresh, fetchEvents])

  const filtered = events.filter((e) => {
    if (cameraFilter && e.cameraId !== cameraFilter) return false
    if (textFilter) {
      const q = textFilter.toLowerCase()
      if (!e.filePath.toLowerCase().includes(q) && !e.cameraId.toLowerCase().includes(q) && !e.streamName.toLowerCase().includes(q))
        return false
    }
    return true
  })

  const eventColor = (e: FsFileEventDto): 'success' | 'default' | 'error' => {
    if (e.eventType === 'deleted') return 'error'
    if (e.retained) return 'success'
    return 'default'
  }

  const formatTs = (iso: string) => new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })

  const canPreview = selected != null && selected.retained && selected.eventType !== 'deleted' && !!selected.cameraId && !!selected.clipUid

  return (
    <Box sx={{ display: 'flex', height: '100%', overflow: 'hidden' }}>
      {/* ── Left: controls + list ── */}
      <Box sx={{ width: 300, flexShrink: 0, display: 'flex', flexDirection: 'column', borderRight: 1, borderColor: 'divider' }}>
        {/* Controls */}
        <Stack direction="row" spacing={1} alignItems="center" sx={{ px: 1, py: 0.5, flexShrink: 0 }}>
          <TextField
            select
            size="small"
            value={cameraFilter}
            onChange={(e) => setCameraFilter(e.target.value)}
            sx={{ width: 110 }}
            SelectProps={{ displayEmpty: true }}
          >
            <MenuItem value="">{t('fsActivity.allCameras')}</MenuItem>
            {cameraIds.map((id) => (
              <MenuItem key={id} value={id}>{id}</MenuItem>
            ))}
          </TextField>
          <TextField
            size="small"
            placeholder={t('fsActivity.filterHint')}
            value={textFilter}
            onChange={(e) => setTextFilter(e.target.value)}
            sx={{ flex: 1 }}
          />
        </Stack>
        <Stack direction="row" spacing={1} alignItems="center" sx={{ px: 1, pb: 0.5, flexShrink: 0 }}>
          <FormControlLabel
            control={<Switch size="small" checked={autoRefresh} onChange={(_, v) => setAutoRefresh(v)} />}
            label={<Typography variant="caption">{t('fsActivity.autoRefresh')}</Typography>}
            sx={{ mr: 0 }}
          />
          <Button size="small" variant="outlined" onClick={() => void fetchEvents()} sx={{ minWidth: 0, py: 0.25, px: 1, fontSize: '0.7rem' }}>
            {t('common.refresh')}
          </Button>
          <Typography variant="caption" color="text.secondary">{filtered.length}</Typography>
        </Stack>
        <Divider />

        {/* Event list */}
        <Box sx={{ flex: 1, overflowY: 'auto' }}>
          {filtered.length === 0 && (
            <Typography variant="body2" color="text.secondary" sx={{ p: 1 }}>{t('fsActivity.noEvents')}</Typography>
          )}
          {filtered.map((e, i) => (
            <Box
              key={`${e.clipUid}-${i}`}
              onClick={() => setSelected(e)}
              sx={{
                px: 1, py: 0.5, cursor: 'pointer', borderBottom: '1px solid', borderColor: 'divider',
                bgcolor: selected === e ? 'action.selected' : 'transparent',
                '&:hover': { bgcolor: 'action.hover' },
                opacity: e.eventType === 'deleted' ? 0.65 : 1,
              }}
            >
              <Stack direction="row" spacing={0.5} alignItems="center">
                <Chip
                  label={e.eventType === 'deleted' ? t('fsActivity.deleted') : (e.retained ? t('fsActivity.retained') : t('fsActivity.temp'))}
                  color={eventColor(e)}
                  size="small"
                  sx={{ height: 16, fontSize: '0.6rem', '& .MuiChip-label': { px: 0.5 } }}
                />
                <Typography variant="caption" color="text.secondary" sx={{ flexShrink: 0 }}>
                  {formatTs(e.occurredAt)}
                </Typography>
              </Stack>
              <Typography variant="caption" noWrap sx={{ display: 'block', textDecoration: e.eventType === 'deleted' ? 'line-through' : 'none', color: 'text.secondary' }}>
                {e.cameraId}{e.streamName ? ` / ${e.streamName}` : ''}
              </Typography>
              <Typography variant="caption" noWrap sx={{ display: 'block', fontFamily: 'monospace' }}>
                {basename(e.filePath)}
              </Typography>
            </Box>
          ))}
        </Box>
      </Box>

      {/* ── Middle: details ── */}
      <Box sx={{ width: 220, flexShrink: 0, overflowY: 'auto', borderRight: 1, borderColor: 'divider', p: 1.5 }}>
        {!selected ? (
          <Typography variant="body2" color="text.secondary">{t('fsActivity.selectHint')}</Typography>
        ) : (
          <Stack spacing={1}>
            <Chip
              label={selected.eventType === 'deleted' ? t('fsActivity.deleted') : (selected.retained ? t('fsActivity.retained') : t('fsActivity.temp'))}
              color={eventColor(selected)}
              size="small"
              sx={{ alignSelf: 'flex-start' }}
            />

            <Box>
              <Typography variant="caption" color="text.secondary">{t('fsActivity.camera')}</Typography>
              <Typography variant="body2">{selected.cameraId || '—'}</Typography>
            </Box>

            {selected.streamName && (
              <Box>
                <Typography variant="caption" color="text.secondary">{t('fsActivity.stream')}</Typography>
                <Typography variant="body2">{selected.streamName}</Typography>
              </Box>
            )}

            <Box>
              <Typography variant="caption" color="text.secondary">{t('fsActivity.time')}</Typography>
              <Typography variant="body2">{new Date(selected.occurredAt).toLocaleString()}</Typography>
            </Box>

            {selected.endTs && (
              <Box>
                <Typography variant="caption" color="text.secondary">{t('fsActivity.endTs')}</Typography>
                <Typography variant="body2">{new Date(selected.endTs).toLocaleString()}</Typography>
              </Box>
            )}

            <Box>
              <Typography variant="caption" color="text.secondary">{t('fsActivity.size')}</Typography>
              <Typography variant="body2">{selected.sizeBytes > 0 ? formatBytes(selected.sizeBytes) : '—'}</Typography>
            </Box>

            <Box>
              <Typography variant="caption" color="text.secondary">{t('fsActivity.filePath')}</Typography>
              <Typography variant="caption" sx={{ display: 'block', fontFamily: 'monospace', wordBreak: 'break-all', bgcolor: 'action.hover', p: 0.5, borderRadius: 0.5 }}>
                {selected.filePath}
              </Typography>
            </Box>

            {selected.retained && selected.eventType !== 'deleted' && (
              <Button
                component="a"
                href={`/api/arch/hls/ts?clipUid=${encodeURIComponent(selected.clipUid)}`}
                download
                size="small"
                variant="outlined"
                startIcon={<DownloadIcon />}
                sx={{ alignSelf: 'flex-start' }}
              >
                {t('fsActivity.download')}
              </Button>
            )}
          </Stack>
        )}
      </Box>

      {/* ── Right: video preview ── */}
      <Box sx={{ flex: 1, minWidth: 0, overflow: 'hidden' }}>
        {canPreview ? (
          <ClipPlayer key={selected!.clipUid} clipUid={selected!.clipUid} cameraId={selected!.cameraId} />
        ) : (
          <Box sx={{ height: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
            <Typography variant="body2" color="text.secondary">
              {selected ? t('fsActivity.noPreview') : t('fsActivity.selectHint')}
            </Typography>
          </Box>
        )}
      </Box>
    </Box>
  )
}
