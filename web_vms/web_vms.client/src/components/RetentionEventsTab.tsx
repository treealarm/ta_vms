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
      setSelected(page.events[0] ?? null)
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
          <Typography variant="h6">Retention events</Typography>
          {loading && events.length === 0 && <LinearProgress />}
          {error && (
            <Typography color="error">
              {error}{' '}
              <Button size="small" onClick={() => void resetAndLoad()}>
                Retry
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
                  <TableCell>event_id</TableCell>
                  <TableCell>cam_id</TableCell>
                  <TableCell>alarm_start_ts</TableCell>
                  <TableCell>alarm_end_ts</TableCell>
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
                No rows.
              </Typography>
            )}
          </Box>

          <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap">
            <Button variant="outlined" size="small" disabled={loading || !hasMore} onClick={() => void loadOlder()}>
              Load older
            </Button>
            {loading && events.length > 0 && <LinearProgress sx={{ flex: 1, minWidth: 120 }} />}
          </Stack>

          <Divider />

          <Typography variant="subtitle2" color="text.secondary">
            Archive preview (HLS at alarm_start_ts; alarm end shown for reference)
          </Typography>
          <ArchiveHlsPreview selected={selected} />
        </Stack>
      </CardContent>
    </Card>
  )
}
