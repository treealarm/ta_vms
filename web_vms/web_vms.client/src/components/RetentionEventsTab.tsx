import { useCallback, useEffect, useState } from 'react'
import {
  Box,
  Button,
  Card,
  CardContent,
  LinearProgress,
  List,
  ListItemButton,
  MenuItem,
  Stack,
  TextField,
  Typography
} from '@mui/material'
import { useTranslation } from 'react-i18next'

import type { RetentionEventRowDto, RetentionEventsListDto } from '../dto/dtos'
import { useAppSelector } from '../store'
import { getErrorMessage } from '../store/api'
import ArchiveHlsPreview from './ArchiveHlsPreview'
import EventDetailsPanel from './EventDetailsPanel'

const PAGE_SIZE = 50
const COLUMN_MAX_HEIGHT = 'min(75vh, 900px)'

async function fetchRetentionEventsPage(params: { before?: string }): Promise<RetentionEventsListDto> {
  const qs = new URLSearchParams()
  qs.set('limit', String(PAGE_SIZE))
  if (params.before) {
    qs.set('before', params.before)
  }
  const response = await fetch(`/api/fs/retention-events?${qs.toString()}`)
  if (!response.ok) {
    throw new Error(await getErrorMessage(response))
  }
  return response.json() as Promise<RetentionEventsListDto>
}

export default function RetentionEventsTab() {
  const { t } = useTranslation()
  const camerasMap = useAppSelector((s) => s.live.cameraStatesById)
  const cameraIds = Object.keys(camerasMap).sort()

  const [events, setEvents] = useState<RetentionEventRowDto[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [hasMore, setHasMore] = useState(false)
  const [nextBefore, setNextBefore] = useState<string | undefined>(undefined)
  const [selected, setSelected] = useState<RetentionEventRowDto | null>(null)
  const [cameraFilter, setCameraFilter] = useState('')
  const [textFilter, setTextFilter] = useState('')

  const resetAndLoad = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const page = await fetchRetentionEventsPage({})
      setEvents(page.events)
      setHasMore(page.hasMore)
      setNextBefore(page.nextBeforeAlarmStart)
      setSelected((prev) => prev ?? page.events[0] ?? null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
      setEvents([])
      setHasMore(false)
      setNextBefore(undefined)
      setSelected(null)
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void resetAndLoad()
    const id = setInterval(() => void resetAndLoad(), 15_000)
    return () => clearInterval(id)
  }, [resetAndLoad])

  const filteredEvents = events.filter((e) => {
    if (cameraFilter && e.cameraId !== cameraFilter) return false
    if (textFilter) {
      const q = textFilter.toLowerCase()
      if (!e.eventId.toLowerCase().includes(q) && !e.cameraId.toLowerCase().includes(q) && !(e.streamName ?? '').toLowerCase().includes(q))
        return false
    }
    return true
  })

  const loadOlder = async () => {
    if (!nextBefore || loading) {
      return
    }
    setLoading(true)
    setError(null)
    try {
      const page = await fetchRetentionEventsPage({ before: nextBefore })
      setEvents((prev) => {
        const merged = [...prev, ...page.events]
        const seen = new Set<string>()
        return merged
          .filter((e) => seen.has(e.eventId) ? false : (seen.add(e.eventId), true))
          .sort((a, b) => new Date(b.alarmStartTs).getTime() - new Date(a.alarmStartTs).getTime())
      })
      setHasMore(page.hasMore)
      setNextBefore(page.nextBeforeAlarmStart)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }

  return (
    <Card variant="outlined">
      <CardContent sx={{ p: 2 }}>
        <Stack spacing={2}>
          <Typography variant="h6">{t('events.title')}</Typography>
          {loading && events.length === 0 && <LinearProgress />}
          {error && (
            <Typography color="error">
              {error}{' '}
              <Button size="small" onClick={() => void resetAndLoad()}>
                {t('events.retry')}
              </Button>
            </Typography>
          )}

          <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} alignItems="flex-start">
            {/* Left: compact event list */}
            <Box sx={{ width: { xs: '100%', md: 260 }, flexShrink: 0 }}>
              <Stack direction="row" spacing={1} sx={{ mb: 1 }}>
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
                  sx={{ flex: 1, minWidth: 0 }}
                />
              </Stack>

              <Box
                sx={{
                  maxHeight: { xs: 240, md: COLUMN_MAX_HEIGHT },
                  overflow: 'auto',
                  border: 1,
                  borderColor: 'divider',
                  borderRadius: 1
                }}
              >
                <List dense disablePadding>
                  {filteredEvents.map((row) => (
                    <ListItemButton
                      key={row.eventId}
                      selected={selected?.eventId === row.eventId}
                      onClick={() => setSelected(row)}
                      sx={{ display: 'block', borderBottom: 1, borderColor: 'divider' }}
                    >
                      <Typography variant="body2" fontWeight="medium" noWrap>
                        {row.cameraId}{row.streamName ? ` / ${row.streamName}` : ''}
                      </Typography>
                      <Typography variant="caption" color="text.secondary" component="div">
                        {new Date(row.alarmStartTs).toLocaleString()}
                      </Typography>
                      <Typography variant="caption" color="text.secondary" component="div">
                        {row.alarmEndTs ? new Date(row.alarmEndTs).toLocaleTimeString() : t('events.active')}
                      </Typography>
                    </ListItemButton>
                  ))}
                </List>
                {filteredEvents.length === 0 && !loading && (
                  <Typography variant="body2" color="text.secondary" sx={{ py: 2, px: 1 }}>
                    {t('events.noRows')}
                  </Typography>
                )}
              </Box>

              <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap" sx={{ mt: 1 }}>
                <Button variant="outlined" size="small" disabled={loading} onClick={() => void resetAndLoad()}>
                  {t('events.refresh')}
                </Button>
                <Button variant="outlined" size="small" disabled={loading || !hasMore} onClick={() => void loadOlder()}>
                  {t('events.loadOlder')}
                </Button>
                {loading && events.length > 0 && <LinearProgress sx={{ flex: 1, minWidth: 80 }} />}
              </Stack>
            </Box>

            {/* Middle: archive preview */}
            <Box sx={{ flex: 2, minWidth: 0, width: '100%' }}>
              <ArchiveHlsPreview selected={selected} />
            </Box>

            {/* Right: extended details */}
            <Box
              sx={{
                width: { xs: '100%', md: 320 },
                flexShrink: 0,
                maxHeight: { xs: 'none', md: COLUMN_MAX_HEIGHT },
                overflow: 'auto'
              }}
            >
              <EventDetailsPanel selected={selected} />
            </Box>
          </Stack>
        </Stack>
      </CardContent>
    </Card>
  )
}
