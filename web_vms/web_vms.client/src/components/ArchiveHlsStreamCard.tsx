import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import AccessTimeIcon from '@mui/icons-material/AccessTime'
import { Alert, Box, Card, CardContent, IconButton, Stack, Tooltip, Typography } from '@mui/material'
import Hls from 'hls.js'

import type { RecordingDto, RecordingRangeDto } from '../dto/dtos'
import RecordingsList from './RecordingsList'

type ArchiveHlsStreamCardProps = {
  cameraId: string
}

/** Archive playback via HLS (explicit clip list from recording-ranges). WebRTC arch player is not used. */
export default function ArchiveHlsStreamCard({ cameraId }: ArchiveHlsStreamCardProps) {
  const videoRef = useRef<HTMLVideoElement>(null)
  const [hlsError, setHlsError] = useState<string | null>(null)
  const [listOpen, setListOpen] = useState(true)
  const [anchorTs, setAnchorTs] = useState(() => new Date().toISOString())
  const [ranges, setRanges] = useState<RecordingRangeDto[]>([])
  const [startIndex, setStartIndex] = useState(0)
  const rangesRef = useRef<RecordingRangeDto[]>([])
  rangesRef.current = ranges

  const onRangesChange = useCallback((r: RecordingRangeDto[]) => {
    setRanges(r)
    setStartIndex(0)
  }, [])

  const handleSelectRecording = (rec: RecordingDto) => {
    const i = ranges.findIndex((x) => x.recordings[0]?.filePath === rec.filePath && x.recordings[0]?.startTime === rec.startTime)
    if (i >= 0) {
      setStartIndex(i)
    }
  }

  const playlistUrl = useMemo(() => {
    if (ranges.length === 0 || startIndex < 0 || startIndex >= ranges.length) return null
    const range = ranges[startIndex]
    const firstClip = range.recordings[0]
    if (!cameraId || !firstClip?.clipUid) return null
    const q = new URLSearchParams({
      cameraId,
      clipUid: firstClip.clipUid,
      count: String(range.recordings.length),
    })
    return `/api/arch/hls/from-files.m3u8?${q.toString()}`
  }, [cameraId, ranges, startIndex])

  const rangeFilePath = ranges[startIndex]?.recordings[0]?.filePath

  useEffect(() => {
    const video = videoRef.current
    setHlsError(null)

    if (!video || !playlistUrl) {
      return
    }

    let hls: Hls | null = null

    const tryPlay = () => {
      void video.play().catch(() => {
        /* autoplay policy */
      })
    }

    const onFatal = (detail: string) => {
      setHlsError(detail)
    }

    const advanceToNextClip = () => {
      setStartIndex((i) => {
        const list = rangesRef.current
        if (i < list.length - 1) {
          return i + 1
        }
        return i
      })
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

    video.addEventListener('ended', advanceToNextClip)

    return () => {
      video.removeEventListener('ended', advanceToNextClip)
      video.onerror = null
      video.removeEventListener('canplay', tryPlay)
      video.removeEventListener('loadeddata', tryPlay)
      if (hls != null) {
        hls.destroy()
      }
      video.removeAttribute('src')
      video.load()
    }
  }, [playlistUrl])

  return (
    <Card
      sx={{
        height: '100%',
        minHeight: 0,
        display: 'flex',
        flexDirection: 'column',
        overflow: 'hidden',
      }}
    >
      <CardContent
        sx={{
          flex: 1,
          minHeight: 0,
          display: 'flex',
          flexDirection: 'column',
          overflow: 'hidden',
          pt: 2,
          '&:last-child': { pb: 2 },
        }}
      >
        <Stack spacing={1.5} sx={{ flex: 1, minHeight: 0, overflow: 'hidden' }}>
          <Stack direction="row" alignItems="center" justifyContent="space-between" spacing={1} sx={{ flexShrink: 0 }}>
            <Typography variant="body2" color="text.secondary">
              Archive (HLS) — {cameraId}
            </Typography>
            <Tooltip title={listOpen ? 'Hide recordings' : 'Show recordings'}>
              <IconButton size="small" onClick={() => setListOpen((v) => !v)}>
                <AccessTimeIcon />
              </IconButton>
            </Tooltip>
          </Stack>

          {hlsError && (
            <Alert severity="error" onClose={() => setHlsError(null)} sx={{ flexShrink: 0 }}>
              {hlsError}
            </Alert>
          )}

          <Box sx={{ width: '100%', flex: 1, minHeight: 0, display: 'flex' }}>
            <Stack direction="row" spacing={1} sx={{ width: '100%', flex: 1, minHeight: 0 }}>
              <Box sx={{ flex: 1, minWidth: 0, minHeight: 0 }}>
                {!playlistUrl ? (
                  <Box
                    sx={{
                      width: '100%',
                      height: '100%',
                      display: 'flex',
                      alignItems: 'center',
                      justifyContent: 'center',
                      bgcolor: 'black',
                      color: 'text.secondary',
                      fontSize: 14,
                      px: 1,
                      textAlign: 'center',
                    }}
                  >
                    Open the recordings panel and pick a time range, or select a clip to play.
                  </Box>
                ) : (
                  <Box
                    sx={{
                      width: '100%',
                      height: '100%',
                      bgcolor: 'black',
                      borderRadius: 1,
                      overflow: 'hidden',
                      '& video': {
                        width: '100%',
                        height: '100%',
                        display: 'block',
                        objectFit: 'contain',
                      },
                    }}
                  >
                    <video
                      key={playlistUrl}
                      ref={videoRef}
                      controls
                      playsInline
                      muted
                    />
                  </Box>
                )}
              </Box>

              {listOpen && (
                <Box
                  sx={{
                    display: 'flex',
                    flexDirection: 'column',
                    borderLeft: '1px solid rgba(0,0,0,0.1)',
                    overflow: 'hidden',
                    minHeight: 0,
                  }}
                >
                  <RecordingsList
                    open={listOpen}
                    cameraId={cameraId}
                    timestamp={anchorTs}
                    filePath={rangeFilePath}
                    playbackStartTime={ranges[startIndex]?.recordings[0]?.startTime}
                    onSelect={handleSelectRecording}
                    onTimeChange={setAnchorTs}
                    onRangesChange={onRangesChange}
                  />
                </Box>
              )}
            </Stack>
          </Box>
        </Stack>
      </CardContent>
    </Card>
  )
}
