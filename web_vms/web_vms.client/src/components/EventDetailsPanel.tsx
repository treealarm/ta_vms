import { useEffect, useState } from 'react'
import { Box, Chip, Dialog, Divider, List, ListItem, Stack, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'

import type { AnalyticsDetectionDto, EpisodeDto, RecordingDto } from '../dto/dtos'
import { getErrorMessage } from '../store/api'
import { useCameraName } from '../store'

type Props = {
  selected: EpisodeDto | null
}

export type EventSource = 'analytics' | 'onvif' | 'other'

// Source is derived from real fields, not the (now opaque) event id: analytics episodes carry an
// object_id from re-identification; ONVIF still uses an "onvif-" prefixed event_id.
export function eventSource(ep: { eventId: string; objectId?: string }): EventSource {
  if (ep.objectId) return 'analytics'
  if (ep.eventId.startsWith('onvif-')) return 'onvif'
  return 'other'
}

async function fetchEventDetections(eventUuid: string, signal: AbortSignal): Promise<AnalyticsDetectionDto[]> {
  const resp = await fetch(`/api/analytics/detections?eventUuid=${encodeURIComponent(eventUuid)}&limit=100`, { signal })
  if (!resp.ok) throw new Error(await getErrorMessage(resp))
  return (await resp.json()) as AnalyticsDetectionDto[]
}

function DetectionsBlock({ eventUuid }: { eventUuid: string }) {
  const { t } = useTranslation()
  const [detections, setDetections] = useState<AnalyticsDetectionDto[]>([])
  // <img src="/api/..."> can't carry the Bearer token that the global fetch wrapper adds, so
  // crops are fetched once into blob URLs shared by the thumbnail and the zoom dialog.
  const [cropUrls, setCropUrls] = useState<Record<number, string>>({})
  const [zoom, setZoom] = useState<number | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    fetchEventDetections(eventUuid, controller.signal)
      .then(setDetections)
      .catch(() => setDetections([]))
    return () => controller.abort()
  }, [eventUuid])

  useEffect(() => {
    let cancelled = false
    const urls: string[] = []
    setCropUrls({})
    for (const d of detections) {
      if (!d.hasCrop) continue
      void fetch(`/api/analytics/detections/${d.id}/crop`)
        .then((r) => (r.ok ? r.blob() : Promise.reject(new Error(`HTTP ${r.status}`))))
        .then((blob) => {
          if (cancelled) return
          const url = URL.createObjectURL(blob)
          urls.push(url)
          setCropUrls((prev) => ({ ...prev, [d.id]: url }))
        })
        .catch(() => { /* broken crop — placeholder stays */ })
    }
    return () => {
      cancelled = true
      urls.forEach((u) => URL.revokeObjectURL(u))
    }
  }, [detections])

  if (detections.length === 0) return null

  const cropSx = { height: 64, width: 96, objectFit: 'contain', borderRadius: 0.5, flexShrink: 0 } as const

  return (
    <>
      <Divider />
      <Box>
        <Typography variant="subtitle2" gutterBottom>
          {t('events.detections')} ({detections.length})
        </Typography>
        <Stack spacing={1}>
          {detections.map((d) => (
            <Stack key={d.id} direction="row" spacing={1} alignItems="center">
              {d.hasCrop && (cropUrls[d.id] ? (
                <Box component="img" src={cropUrls[d.id]} alt={d.kind} onClick={() => setZoom(d.id)}
                  sx={{ ...cropSx, cursor: 'pointer' }} />
              ) : (
                <Box sx={{ ...cropSx, bgcolor: 'action.hover' }} />
              ))}
              <Stack sx={{ minWidth: 0 }}>
                <Stack direction="row" spacing={0.5} alignItems="center" flexWrap="wrap">
                  <Chip label={t(`analytics.class.${d.kind}`, d.kind)} size="small" />
                  <Typography variant="caption" color="text.secondary">
                    {(d.confidence * 100).toFixed(0)}%
                  </Typography>
                  {d.plateText && <Chip label={d.plateText} size="small" color="primary" variant="outlined" />}
                </Stack>
                <Typography variant="caption" color="text.secondary">
                  {new Date(d.detectedAt).toLocaleTimeString()}
                </Typography>
              </Stack>
            </Stack>
          ))}
        </Stack>
      </Box>

      <Dialog open={zoom !== null} onClose={() => setZoom(null)} maxWidth="md">
        {zoom !== null && cropUrls[zoom] && (
          <Box component="img" src={cropUrls[zoom]} alt="" sx={{ maxWidth: '90vw', maxHeight: '90vh' }} />
        )}
      </Dialog>
    </>
  )
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

function formatDuration(ms: number): string {
  const totalSec = Math.max(0, Math.round(ms / 1000))
  const h = Math.floor(totalSec / 3600)
  const m = Math.floor((totalSec % 3600) / 60)
  const s = totalSec % 60
  const parts: string[] = []
  if (h > 0) parts.push(`${h}h`)
  if (h > 0 || m > 0) parts.push(`${m}m`)
  parts.push(`${s}s`)
  return parts.join(' ')
}

async function fetchEventClips(eventUuid: string, signal: AbortSignal): Promise<RecordingDto[]> {
  const resp = await fetch(`/api/fs/event-clips?eventUuid=${encodeURIComponent(eventUuid)}`, { signal })
  if (!resp.ok) throw new Error(await getErrorMessage(resp))
  const data = (await resp.json()) as { clips: RecordingDto[] }
  return data.clips
}

function DetailRow({ label, value }: { label: string; value: string }) {
  return (
    <Stack direction="row" spacing={1} justifyContent="space-between">
      <Typography variant="body2" color="text.secondary">{label}</Typography>
      <Typography variant="body2" sx={{ wordBreak: 'break-all', textAlign: 'right' }}>{value}</Typography>
    </Stack>
  )
}

export default function EventDetailsPanel({ selected }: Props) {
  const { t } = useTranslation()
  const cameraName = useCameraName(selected?.cameraId)
  const [clips, setClips] = useState<RecordingDto[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!selected) {
      setClips([])
      setError(null)
      return
    }

    setLoading(true)
    setError(null)
    const controller = new AbortController()

    fetchEventClips(selected.eventUuid, controller.signal)
      .then(setClips)
      .catch((e: unknown) => {
        if (e instanceof DOMException && e.name === 'AbortError') return
        setError(e instanceof Error ? e.message : String(e))
        setClips([])
      })
      .finally(() => setLoading(false))

    return () => controller.abort()
  }, [selected])

  if (!selected) {
    return (
      <Typography variant="body2" color="text.secondary">
        {t('archive.selectHint')}
      </Typography>
    )
  }

  const start = new Date(selected.alarmStartTs)
  const end = selected.alarmEndTs ? new Date(selected.alarmEndTs) : null
  const duration = end ? formatDuration(end.getTime() - start.getTime()) : null

  return (
    <Stack spacing={1.5}>
      <Box>
        <Typography variant="subtitle2" gutterBottom>{t('events.details')}</Typography>
        <Stack spacing={0.5}>
          <DetailRow
            label={t('events.source')}
            value={{
              analytics: t('events.sourceAnalytics'),
              onvif: t('events.sourceOnvif'),
              other: t('events.sourceOther'),
            }[eventSource(selected)]}
          />
          <DetailRow label={t('cameras.cameraName')} value={cameraName} />
          {selected.streamName && <DetailRow label={t('events.streamName')} value={selected.streamName} />}
          <DetailRow label={t('events.alarmStart')} value={start.toLocaleString()} />
          <DetailRow label={t('events.alarmEnd')} value={end ? end.toLocaleString() : t('events.active')} />
          {duration && <DetailRow label={t('events.duration')} value={duration} />}
        </Stack>
      </Box>

      {eventSource(selected) === 'analytics' && <DetectionsBlock eventUuid={selected.eventUuid} />}

      <Divider />

      <Box>
        <Typography variant="subtitle2" gutterBottom>
          {t('events.clipsList')}{clips.length > 0 ? ` (${clips.length})` : ''}
        </Typography>
        {loading && (
          <Typography variant="body2" color="text.secondary">{t('archive.loadingStreams')}</Typography>
        )}
        {error && (
          <Typography variant="body2" color="error">{error}</Typography>
        )}
        {!loading && !error && clips.length === 0 && (
          <Typography variant="body2" color="text.secondary">{t('archive.noClips')}</Typography>
        )}
        {clips.length > 0 && (
          <List dense disablePadding>
            {clips.map((c) => (
              <ListItem key={c.clipUid} disableGutters sx={{ display: 'block', py: 0.5 }}>
                <Typography variant="body2" noWrap title={c.filePath}>{basename(c.filePath)}</Typography>
                <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap">
                  {c.streamName && <Chip label={c.streamName} size="small" variant="outlined" />}
                  <Typography variant="caption" color="text.secondary">
                    {new Date(c.startTime).toLocaleString()}
                    {c.endTime ? ` – ${new Date(c.endTime).toLocaleTimeString()}` : ''}
                  </Typography>
                  <Typography variant="caption" color="text.secondary">{formatBytes(c.sizeBytes)}</Typography>
                </Stack>
              </ListItem>
            ))}
          </List>
        )}
      </Box>
    </Stack>
  )
}
