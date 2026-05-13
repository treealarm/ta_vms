import { useEffect, useRef, useState } from 'react'
import { Alert, Box, Stack, Tab, Tabs, Typography } from '@mui/material'
import Hls from 'hls.js'

import type { RetentionEventRowDto } from '../dto/dtos'

type StreamEntry = {
  streamName: string
  playlistUrl: string
}

type Props = {
  selected: RetentionEventRowDto | null
}

async function fetchEventPlaylists(eventId: string): Promise<StreamEntry[]> {
  const resp = await fetch(`/api/fs/event-playlists?eventId=${encodeURIComponent(eventId)}`)
  if (!resp.ok) throw new Error(`Failed to load streams: ${resp.status}`)
  const data = (await resp.json()) as { streams: StreamEntry[] }
  return data.streams
}

export default function ArchiveHlsPreview({ selected }: Props) {
  const videoRef = useRef<HTMLVideoElement>(null)
  const [streams, setStreams] = useState<StreamEntry[]>([])
  const [activeStream, setActiveStream] = useState<string>('')
  const [loadingStreams, setLoadingStreams] = useState(false)
  const [hlsError, setHlsError] = useState<string | null>(null)

  useEffect(() => {
    if (!selected) {
      setStreams([])
      setActiveStream('')
      return
    }

    setLoadingStreams(true)
    setHlsError(null)

    fetchEventPlaylists(selected.eventId)
      .then((entries) => {
        setStreams(entries)
        const preferred = selected.streamName
        const pick = entries.find((e) => e.streamName === preferred) ?? entries[0]
        setActiveStream(pick?.streamName ?? '')
      })
      .catch((e: unknown) => {
        setHlsError(e instanceof Error ? e.message : String(e))
        setStreams([])
        setActiveStream('')
      })
      .finally(() => setLoadingStreams(false))
  }, [selected])

  useEffect(() => {
    const video = videoRef.current
    if (!video || !selected || !activeStream) return

    const entry = streams.find((s) => s.streamName === activeStream)
    if (!entry) return

    setHlsError(null)
    const { playlistUrl } = entry
    let hls: Hls | null = null

    const tryPlay = () => void video.play().catch(() => {})
    const onFatal = (detail: string) => setHlsError(detail)

    if (Hls.isSupported()) {
      hls = new Hls({ enableWorker: true, lowLatencyMode: false })
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
  }, [selected, activeStream, streams])

  if (!selected) {
    return (
      <Typography variant="body2" color="text.secondary">
        Select an event row to load archive preview (HLS).
      </Typography>
    )
  }

  const startLabel = new Date(selected.alarmStartTs).toLocaleString()
  const endLabel = selected.alarmEndTs ? new Date(selected.alarmEndTs).toLocaleString() : '—'

  return (
    <Stack spacing={1}>
      <Typography variant="subtitle2">
        Camera <strong>{selected.cameraId}</strong> · alarm start {startLabel} · alarm end {endLabel}
      </Typography>

      {loadingStreams && (
        <Typography variant="body2" color="text.secondary">
          Loading streams…
        </Typography>
      )}

      {!loadingStreams && streams.length > 1 && (
        <Tabs
          value={activeStream}
          onChange={(_, v: string) => setActiveStream(v)}
          variant="scrollable"
          scrollButtons="auto"
          sx={{ minHeight: 34, '& .MuiTab-root': { minHeight: 34, py: 0.5 } }}
        >
          {streams.map((s) => (
            <Tab key={s.streamName} label={s.streamName} value={s.streamName} />
          ))}
        </Tabs>
      )}

      {hlsError && (
        <Alert severity="error" onClose={() => setHlsError(null)}>
          {hlsError}
        </Alert>
      )}

      {!loadingStreams && streams.length === 0 && !hlsError && (
        <Typography variant="body2" color="text.secondary">
          No clips found for this event.
        </Typography>
      )}

      <Box
        sx={{
          width: '100%',
          aspectRatio: '16 / 9',
          maxHeight: 'min(75vh, 900px)',
          borderRadius: 1,
          overflow: 'hidden',
          bgcolor: 'black',
          '& video': { width: '100%', height: '100%', display: 'block', objectFit: 'contain' }
        }}
      >
        <video
          key={`${selected.eventId}-${activeStream}`}
          ref={videoRef}
          controls
          playsInline
          muted
        />
      </Box>
    </Stack>
  )
}
