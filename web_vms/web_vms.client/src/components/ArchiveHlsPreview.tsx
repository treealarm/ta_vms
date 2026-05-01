import { useEffect, useRef, useState } from 'react'
import { Alert, Box, Stack, Typography } from '@mui/material'
import Hls from 'hls.js'

import type { RetentionEventRowDto } from '../dto/dtos'

type ArchiveHlsPreviewProps = {
  selected: RetentionEventRowDto | null
}

export default function ArchiveHlsPreview({ selected }: ArchiveHlsPreviewProps) {
  const videoRef = useRef<HTMLVideoElement>(null)
  const [hlsError, setHlsError] = useState<string | null>(null)

  useEffect(() => {
    const video = videoRef.current
    setHlsError(null)

    if (!video || !selected) {
      return
    }

    const from = new Date(selected.alarmStartTs)
    let to = selected.alarmEndTs ? new Date(selected.alarmEndTs) : new Date(from.getTime() + 60_000)
    if (Number.isNaN(from.getTime()) || Number.isNaN(to.getTime())) {
      setHlsError('Invalid alarm times')
      return
    }
    if (to.getTime() <= from.getTime()) {
      to = new Date(from.getTime() + 1000)
    }

    const playlistUrl =
      `/api/arch/hls/preview.m3u8?cameraId=${encodeURIComponent(selected.cameraId)}` +
      `&from=${encodeURIComponent(from.toISOString())}` +
      `&to=${encodeURIComponent(to.toISOString())}`

    let hls: Hls | null = null

    const tryPlay = () => {
      void video.play().catch(() => {
        /* autoplay policy — user can press play on controls */
      })
    }

    const onFatal = (detail: string) => {
      setHlsError(detail)
    }

    if (Hls.isSupported()) {
      hls = new Hls({
        enableWorker: true,
        lowLatencyMode: false,
        xhrSetup: (xhr) => {
          xhr.withCredentials = false
        }
      })
      video.addEventListener('canplay', tryPlay, { once: true })
      video.addEventListener('loadeddata', tryPlay, { once: true })
      hls.on(Hls.Events.MEDIA_ATTACHED, () => {
        hls?.loadSource(playlistUrl)
      })
      hls.on(Hls.Events.MANIFEST_PARSED, () => {
        tryPlay()
      })
      hls.on(Hls.Events.ERROR, (_, data) => {
        if (import.meta.env.DEV) {
          console.warn('HLS event', data.type, data.details, data)
        }
        if (data.fatal) {
          switch (data.type) {
            case Hls.ErrorTypes.NETWORK_ERROR:
              onFatal(`Network error (${data.details}). Check API / proxy (playlist or TS segment).`)
              break
            case Hls.ErrorTypes.MEDIA_ERROR:
              onFatal(`Media error (${data.details}). Codec or TS format may be unsupported in browser.`)
              try {
                hls?.recoverMediaError()
              } catch {
                /* ignore */
              }
              break
            default:
              onFatal(`Playback error: ${data.type} ${data.details ?? ''}`)
          }
        }
      })
      hls.attachMedia(video)
    } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
      video.addEventListener('canplay', tryPlay, { once: true })
      video.addEventListener('loadeddata', tryPlay, { once: true })
      video.src = playlistUrl
      video.onerror = () => {
        onFatal('Native HLS failed to load (invalid playlist or segment URL)')
      }
    } else {
      setHlsError('HLS is not supported in this browser')
      return
    }

    return () => {
      video.onerror = null
      video.removeEventListener('canplay', tryPlay)
      video.removeEventListener('loadeddata', tryPlay)
      if (hls != null) {
        hls.destroy()
      }
      video.removeAttribute('src')
      video.load()
    }
  }, [selected])

  if (!selected) {
    return (
      <Typography variant="body2" color="text.secondary">
        Select an event row to load archive preview for the alarm window (HLS).
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
      {hlsError && (
        <Alert severity="error" onClose={() => setHlsError(null)}>
          {hlsError}
        </Alert>
      )}
      <Box
        sx={{
          width: '100%',
          aspectRatio: '16 / 9',
          maxHeight: 'min(75vh, 900px)',
          borderRadius: 1,
          overflow: 'hidden',
          bgcolor: 'black',
          '& video': {
            width: '100%',
            height: '100%',
            display: 'block',
            objectFit: 'contain'
          }
        }}
      >
        <video
          key={`${selected.eventId}-${selected.cameraId}-${selected.alarmStartTs}`}
          ref={videoRef}
          controls
          playsInline
          muted
        />
      </Box>
    </Stack>
  )
}
