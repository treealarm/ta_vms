import { useEffect, useState } from 'react'
import { Box, Chip, Divider, List, ListItem, Stack, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'

import type { RecordingDto, RetentionEventRowDto } from '../dto/dtos'
import { getErrorMessage } from '../store/api'
import { useCameraName } from '../store'

type Props = {
  selected: RetentionEventRowDto | null
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

async function fetchEventClips(eventId: string, signal: AbortSignal): Promise<RecordingDto[]> {
  const resp = await fetch(`/api/fs/event-clips?eventId=${encodeURIComponent(eventId)}`, { signal })
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

    fetchEventClips(selected.eventId, controller.signal)
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
          <DetailRow label={t('events.eventId')} value={selected.eventId} />
          <DetailRow label={t('cameras.cameraName')} value={cameraName} />
          {selected.streamName && <DetailRow label={t('events.streamName')} value={selected.streamName} />}
          <DetailRow label={t('events.alarmStart')} value={start.toLocaleString()} />
          <DetailRow label={t('events.alarmEnd')} value={end ? end.toLocaleString() : t('events.active')} />
          {duration && <DetailRow label={t('events.duration')} value={duration} />}
        </Stack>
      </Box>

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
