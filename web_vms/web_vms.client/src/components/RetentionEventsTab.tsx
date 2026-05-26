import { useCallback, useEffect, useState } from 'react'
import {
  Box,
  Button,
  Card,
  CardContent,
  Divider,
  LinearProgress,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Typography
} from '@mui/material'
import { useTranslation } from 'react-i18next'

import type { RetentionEventRowDto, RetentionEventsListDto } from '../dto/dtos'
import { getErrorMessage } from '../store/api'
import ArchiveHlsPreview from './ArchiveHlsPreview'

const PAGE_SIZE = 50

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
  const [events, setEvents] = useState<RetentionEventRowDto[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [hasMore, setHasMore] = useState(false)
  const [nextBefore, setNextBefore] = useState<string | undefined>(undefined)
  const [selected, setSelected] = useState<RetentionEventRowDto | null>(null)

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

  const loadOlder = async () => {
    if (!nextBefore || loading) {
      return
    }
    setLoading(true)
    setError(null)
    try {
      const page = await fetchRetentionEventsPage({ before: nextBefore })
      setEvents((prev) => [...prev, ...page.events])
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

          <Box
            sx={{
              width: '100%',
              maxHeight: { xs: 220, sm: 260 },
              overflow: 'auto',
              border: 1,
              borderColor: 'divider',
              borderRadius: 1
            }}
          >
            <Table size="small" stickyHeader>
              <TableHead>
                <TableRow>
                  <TableCell>{t('events.eventId')}</TableCell>
                  <TableCell>{t('events.camId')}</TableCell>
                  <TableCell>{t('events.alarmStart')}</TableCell>
                  <TableCell>{t('events.alarmEnd')}</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {events.map((row) => (
                  <TableRow
                    key={row.eventId}
                    hover
                    selected={selected?.eventId === row.eventId}
                    onClick={() => setSelected(row)}
                    sx={{ cursor: 'pointer' }}
                  >
                    <TableCell>{row.eventId}</TableCell>
                    <TableCell>{row.cameraId}</TableCell>
                    <TableCell>{new Date(row.alarmStartTs).toLocaleString()}</TableCell>
                    <TableCell>{row.alarmEndTs ? new Date(row.alarmEndTs).toLocaleString() : '—'}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
            {events.length === 0 && !loading && (
              <Typography variant="body2" color="text.secondary" sx={{ py: 2, px: 1 }}>
                {t('events.noRows')}
              </Typography>
            )}
          </Box>

          <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap">
            <Button variant="outlined" size="small" disabled={loading} onClick={() => void resetAndLoad()}>
              {t('events.refresh')}
            </Button>
            <Button variant="outlined" size="small" disabled={loading || !hasMore} onClick={() => void loadOlder()}>
              {t('events.loadOlder')}
            </Button>
            {loading && events.length > 0 && <LinearProgress sx={{ flex: 1, minWidth: 120 }} />}
          </Stack>

          <Divider />

          <Typography variant="subtitle2" color="text.secondary">
            {t('events.archivePreview')}
          </Typography>
          <ArchiveHlsPreview selected={selected} />
        </Stack>
      </CardContent>
    </Card>
  )
}
