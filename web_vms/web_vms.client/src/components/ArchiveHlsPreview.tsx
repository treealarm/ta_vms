import { useEffect, useRef, useState } from 'react'
import { Alert, Box, IconButton, MenuItem, Select, Stack, Tooltip, Typography } from '@mui/material'
import DirectionsRunIcon from '@mui/icons-material/DirectionsRun'
import { useTranslation } from 'react-i18next'

import type { EpisodeDto } from '../dto/dtos'
import { useCameraName } from '../store'
import { useHlsPlayer } from '../hooks/useHlsPlayer'
import { playlistWallBounds } from '../utils/playlistTime'
import ArchiveBboxOverlay from './bbox/ArchiveBboxOverlay'
import { useArchiveMetadataCoverage } from '../hooks/archiveMetadataClient'

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
  const [streamsError, setStreamsError] = useState<string | null>(null)

  useEffect(() => {
    if (!selected) {
      setStreams([])
      setActiveUrl(null)
      return
    }

    setLoadingStreams(true)
    setStreamsError(null)

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
        setStreamsError(e instanceof Error ? e.message : String(e))
        setStreams([])
        setActiveUrl(null)
      })
      .finally(() => setLoadingStreams(false))

    return () => controller.abort()
  }, [selected])

  const activeEntry = streams.find((s) => s.playlistUrl === activeUrl) ?? null
  const { error, playlistMap } = useHlsPlayer(videoRef, activeEntry?.playlistUrl ?? null, { autoPlay: true })

  const [dismissedHlsError, setDismissedHlsError] = useState<string | null>(null)
  const hlsError = error && error.detail !== dismissedHlsError
    ? t(
        error.kind === 'notFound'
          ? 'wall.noRecordingAtTime'
          : error.kind === 'network'
            ? 'wall.errorNetwork'
            : error.kind === 'media'
              ? 'wall.errorMedia'
              : 'wall.errorGeneric'
      )
    : null

  // The window the loaded playlist actually covers, which is what decides whether this episode has
  // any recorded geometry to offer.
  const wallBounds = playlistWallBounds(playlistMap)
  const coverage = useArchiveMetadataCoverage(
    selected?.cameraId,
    activeEntry?.streamName ?? '',
    wallBounds?.startMs ?? null,
    wallBounds?.endMs ?? null
  )
  // Local state, not the monitor's slot UI: this panel is opened from the events tab and has no
  // slot, and inventing a key for it would leave entries in monitorUi.slots that nothing cleans up.
  const [bboxOpen, setBboxOpen] = useState(false)
  // Not offered for the "all streams" entry: that playlist interleaves main and sub clips, so there
  // is no single stream whose geometry belongs over the picture, and drawing the camera's boxes
  // across both would put one stream's track ids on the other's frames.
  const showBboxToggle = coverage === 'some' && activeEntry?.streamName != null

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

      {!loadingStreams && (streams.length > 1 || showBboxToggle) && (
        <Stack direction="row" spacing={1} alignItems="center" sx={{ alignSelf: 'flex-start' }}>
          {streams.length > 1 && (
            <Select
              size="small"
              value={activeUrl ?? ''}
              onChange={(e) => setActiveUrl(e.target.value || null)}
              sx={{ fontSize: '0.8rem', height: 28, minWidth: 140 }}
            >
              {streams.map((s) => (
                <MenuItem key={s.playlistUrl} value={s.playlistUrl} sx={{ fontSize: '0.8rem' }}>
                  {entryLabel(s, t('archive.allStreams'), t('archive.default'))}
                </MenuItem>
              ))}
            </Select>
          )}
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
      )}

      {(streamsError ?? hlsError) && (
        <Alert
          severity="error"
          onClose={() => {
            // Both sources feed this one banner, so closing it has to silence whichever is showing.
            setStreamsError(null)
            setDismissedHlsError(error?.detail ?? null)
          }}
        >
          {streamsError ?? hlsError}
        </Alert>
      )}

      {!loadingStreams && streams.length === 0 && !streamsError && (
        <Typography variant="body2" color="text.secondary">
          {t('archive.noClips')}
        </Typography>
      )}

      <Box
        sx={{
          position: 'relative',
          width: '100%',
          aspectRatio: '16 / 9',
          maxHeight: 'min(75vh, 900px)',
          borderRadius: 1,
          overflow: 'hidden',
          bgcolor: 'var(--app-video-bg)',
          '& video': { width: '100%', height: '100%', display: 'block', objectFit: 'contain' }
        }}
      >
        {/* No `key` on the element: useHlsPlayer keys its own state by URL and tears the previous
            source down itself, so remounting here would run the teardown twice. */}
        <video ref={videoRef} controls playsInline muted />
        <ArchiveBboxOverlay
          videoRef={videoRef}
          cameraId={selected.cameraId}
          streamName={activeEntry?.streamName ?? ''}
          playlistMap={playlistMap}
          enabled={bboxOpen && showBboxToggle}
        />
      </Box>
    </Stack>
  )
}
