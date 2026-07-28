import { useEffect, useRef, useState } from 'react'
import { Alert, Box, MenuItem, Select, Stack, Typography } from '@mui/material'
import Hls from 'hls.js'
import { useTranslation } from 'react-i18next'

import { authXhrSetup } from '../auth/authFetch'
import type { EpisodeDto } from '../dto/dtos'
import { useCameraName } from '../store'

type StreamEntry = {
  streamName: string | null  // null = all streams
  playlistUrl: string
  // Present only for a long event split into <=3-min playback chunks; then the label carries the
  // time range and each (stream, chunk) is its own selectable source.
  fromTs?: string
  toTs?: string
}

type Props = {
  selected: EpisodeDto | null
}

async function fetchEventPlaylists(eventUuid: string, signal: AbortSignal): Promise<StreamEntry[]> {
  const resp = await fetch(`/api/fs/event-playlists?eventUuid=${encodeURIComponent(eventUuid)}`, { signal })
  if (!resp.ok) throw new Error(`Failed to load streams: ${resp.status}`)
  const data = (await resp.json()) as { streams: StreamEntry[] }
  return data.streams
}

function entryLabel(e: StreamEntry, allStreamsLabel: string, defaultLabel: string): string {
  const streamPart = e.streamName === null ? allStreamsLabel : (e.streamName || defaultLabel)
  if (!e.fromTs) return streamPart
  const from = new Date(e.fromTs).toLocaleTimeString()
  const to = e.toTs ? new Date(e.toTs).toLocaleTimeString() : ''
  return `${streamPart} · ${from}–${to}`
}

export default function ArchiveHlsPreview({ selected }: Props) {
  const { t } = useTranslation()
  const cameraName = useCameraName(selected?.cameraId)
  const videoRef = useRef<HTMLVideoElement>(null)
  const [streams, setStreams] = useState<StreamEntry[]>([])
  // The selected source is keyed by its playlist URL, not stream name: a long event has several
  // (stream, time-chunk) sources that can share a stream name.
  const [activeUrl, setActiveUrl] = useState<string | null>(null)
  const [loadingStreams, setLoadingStreams] = useState(false)
  const [hlsError, setHlsError] = useState<string | null>(null)

  useEffect(() => {
    if (!selected) {
      setStreams([])
      setActiveUrl(null)
      return
    }

    setLoadingStreams(true)
    setHlsError(null)

    const controller = new AbortController()

    fetchEventPlaylists(selected.eventUuid, controller.signal)
      .then((entries) => {
        setStreams(entries)
        const preferred = selected.streamName ?? null
        const pick =
          (preferred && entries.find((e) => e.streamName === preferred)) ||
          entries.find((e) => e.streamName !== null) ||
          entries[0]
        setActiveUrl(pick?.playlistUrl ?? null)
      })
      .catch((e: unknown) => {
        if (e instanceof DOMException && e.name === 'AbortError') return
        setHlsError(e instanceof Error ? e.message : String(e))
        setStreams([])
        setActiveUrl(null)
      })
      .finally(() => setLoadingStreams(false))

    return () => controller.abort()
  }, [selected])

  useEffect(() => {
    const video = videoRef.current
    if (!video || !selected || !activeUrl) return

    const entry = streams.find((s) => s.playlistUrl === activeUrl)
    if (!entry) return

    setHlsError(null)
    const { playlistUrl } = entry
    let hls: Hls | null = null

    const tryPlay = () => void video.play().catch(() => {})
    const onFatal = (detail: string) => setHlsError(detail)

    if (Hls.isSupported()) {
      hls = new Hls({ enableWorker: true, lowLatencyMode: false, xhrSetup: authXhrSetup })
      video.addEventListener('canplay', tryPlay, { once: true })
      video.addEventListener('loadeddata', tryPlay, { once: true })
      hls.on(Hls.Events.MEDIA_ATTACHED, () => hls?.loadSource(playlistUrl))
      hls.on(Hls.Events.MANIFEST_PARSED, tryPlay)
      hls.on(Hls.Events.ERROR, (_, data) => {
        if (!data.fatal) return
        if (data.type === Hls.ErrorTypes.MEDIA_ERROR) {
          onFatal(`Media error (${data.details})`)
          try { hls?.recoverMediaError() } catch { /* ignore */ }
        } else if (data.type === Hls.ErrorTypes.NETWORK_ERROR) {
          onFatal(`Network error (${data.details}). Check API / proxy.`)
        } else {
          onFatal(`Playback error: ${data.type} ${data.details ?? ''}`)
        }
      })
      hls.attachMedia(video)
    } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
      video.addEventListener('canplay', tryPlay, { once: true })
      video.addEventListener('loadeddata', tryPlay, { once: true })
      video.src = playlistUrl
      video.onerror = () => onFatal('Native HLS failed to load')
    } else {
      setHlsError('HLS is not supported in this browser')
      return
    }

    return () => {
      video.onerror = null
      video.removeEventListener('canplay', tryPlay)
      video.removeEventListener('loadeddata', tryPlay)
      hls?.destroy()
      video.removeAttribute('src')
      video.load()
    }
  }, [selected, activeUrl, streams])

  if (!selected) {
    return (
      <Typography variant="body2" color="text.secondary">
        {t('archive.selectHint')}
      </Typography>
    )
  }

  const startLabel = new Date(selected.alarmStartTs).toLocaleString()
  const endLabel = selected.alarmEndTs ? new Date(selected.alarmEndTs).toLocaleString() : '—'

  return (
    <Stack spacing={1}>
      <Typography variant="subtitle2">
        Camera <strong>{cameraName}</strong> · alarm start {startLabel} · alarm end {endLabel}
      </Typography>

      {loadingStreams && (
        <Typography variant="body2" color="text.secondary">
          {t('archive.loadingStreams')}
        </Typography>
      )}

      {!loadingStreams && streams.length > 1 && (
        <Select
          size="small"
          value={activeUrl ?? ''}
          onChange={(e) => setActiveUrl(e.target.value || null)}
          sx={{ fontSize: '0.8rem', height: 28, alignSelf: 'flex-start', minWidth: 140 }}
        >
          {streams.map((s) => (
            <MenuItem key={s.playlistUrl} value={s.playlistUrl} sx={{ fontSize: '0.8rem' }}>
              {entryLabel(s, t('archive.allStreams'), t('archive.default'))}
            </MenuItem>
          ))}
        </Select>
      )}

      {hlsError && (
        <Alert severity="error" onClose={() => setHlsError(null)}>
          {hlsError}
        </Alert>
      )}

      {!loadingStreams && streams.length === 0 && !hlsError && (
        <Typography variant="body2" color="text.secondary">
          {t('archive.noClips')}
        </Typography>
      )}

      <Box
        sx={{
          width: '100%',
          aspectRatio: '16 / 9',
          maxHeight: 'min(75vh, 900px)',
          borderRadius: 1,
          overflow: 'hidden',
          bgcolor: 'var(--app-video-bg)',
          '& video': { width: '100%', height: '100%', display: 'block', objectFit: 'contain' }
        }}
      >
        <video
          key={activeUrl ?? selected.eventUuid}
          ref={videoRef}
          controls
          playsInline
          muted
        />
      </Box>
    </Stack>
  )
}
