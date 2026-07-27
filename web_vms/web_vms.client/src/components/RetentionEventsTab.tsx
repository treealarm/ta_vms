import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  IconButton,
  LinearProgress,
  List,
  ListItemButton,
  MenuItem,
  Stack,
  TextField,
  Typography
} from '@mui/material'
import ChevronRightIcon from '@mui/icons-material/ChevronRight'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import FiberManualRecordIcon from '@mui/icons-material/FiberManualRecord'
import SubdirectoryArrowRightIcon from '@mui/icons-material/SubdirectoryArrowRight'
import { useTranslation } from 'react-i18next'

import type { EpisodeDto, EventGroupDto, RetentionEventGroupsListDto } from '../dto/dtos'
import { useAppSelector } from '../store'
import { getErrorMessage } from '../store/api'
import ArchiveHlsPreview from './ArchiveHlsPreview'
import EventDetailsPanel, { eventSource } from './EventDetailsPanel'

const PAGE_SIZE = 50
const COLUMN_MAX_HEIGHT = 'min(75vh, 900px)'

async function fetchRetentionEventGroupsPage(params: { before?: string }): Promise<RetentionEventGroupsListDto> {
  const qs = new URLSearchParams()
  qs.set('limit', String(PAGE_SIZE))
  if (params.before) {
    qs.set('before', params.before)
  }
  const response = await fetch(`/api/fs/retention-events?${qs.toString()}`)
  if (!response.ok) {
    throw new Error(await getErrorMessage(response))
  }
  return response.json() as Promise<RetentionEventGroupsListDto>
}

export default function RetentionEventsTab() {
  const { t } = useTranslation()
  const camerasMap = useAppSelector((s) => s.live.cameraStatesById)
  const cameraIds = Object.keys(camerasMap).sort()

  const [groups, setGroups] = useState<EventGroupDto[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [hasMore, setHasMore] = useState(false)
  const [nextBefore, setNextBefore] = useState<string | undefined>(undefined)
  const [selected, setSelected] = useState<EpisodeDto | null>(null)
  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const [cameraFilter, setCameraFilter] = useState('')
  const [textFilter, setTextFilter] = useState('')

  const resetAndLoad = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const page = await fetchRetentionEventGroupsPage({})
      setGroups(page.groups)
      setHasMore(page.hasMore)
      setNextBefore(page.nextBeforeAlarmStart)
      setSelected((prev) => prev ?? page.groups[0]?.episodes[0] ?? null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
      setGroups([])
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

  const cameraLabel = (id: string) => camerasMap[id]?.name || id

  const filteredGroups = useMemo(() => groups.filter((g) => {
    if (cameraFilter && !g.episodes.some((e) => e.cameraId === cameraFilter)) return false
    if (textFilter) {
      const q = textFilter.toLowerCase()
      const hit = g.eventId.toLowerCase().includes(q)
        || (g.objectId ?? '').toLowerCase().includes(q)
        || g.episodes.some((e) => e.cameraId.toLowerCase().includes(q) || (e.streamName ?? '').toLowerCase().includes(q))
      if (!hit) return false
    }
    return true
  }), [groups, cameraFilter, textFilter])

  const loadOlder = async () => {
    if (!nextBefore || loading) {
      return
    }
    setLoading(true)
    setError(null)
    try {
      const page = await fetchRetentionEventGroupsPage({ before: nextBefore })
      setGroups((prev) => {
        const merged = [...prev, ...page.groups]
        const seen = new Set<string>()
        return merged
          .filter((g) => seen.has(g.eventId) ? false : (seen.add(g.eventId), true))
          .sort((a, b) => new Date(b.latestAlarmStart).getTime() - new Date(a.latestAlarmStart).getTime())
      })
      setHasMore(page.hasMore)
      setNextBefore(page.nextBeforeAlarmStart)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }

  const toggleExpanded = (eventId: string) => setExpanded((prev) => {
    const next = new Set(prev)
    if (next.has(eventId)) next.delete(eventId)
    else next.add(eventId)
    return next
  })

  const renderEpisode = (ep: EpisodeDto, opts: { child?: boolean; badge?: number; onToggle?: () => void; isOpen?: boolean }) => (
    <ListItemButton
      key={ep.eventUuid}
      selected={selected?.eventUuid === ep.eventUuid}
      onClick={() => setSelected(ep)}
      sx={{ display: 'block', borderBottom: 1, borderColor: 'divider', pl: opts.child ? 4.5 : 1 }}
    >
      <Stack direction="row" spacing={0.5} alignItems="center">
        {/* Fixed leading slot: expand arrow for a group with children, a dot for a standalone
            singleton, or a nested-arrow marker for a child episode. */}
        <Box sx={{ width: 22, flexShrink: 0, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
          {opts.onToggle ? (
            <IconButton
              size="small"
              edge="start"
              onClick={(e) => { e.stopPropagation(); opts.onToggle?.() }}
              sx={{ p: 0.25 }}
            >
              {opts.isOpen ? <ExpandMoreIcon fontSize="small" /> : <ChevronRightIcon fontSize="small" />}
            </IconButton>
          ) : opts.child ? (
            <SubdirectoryArrowRightIcon sx={{ fontSize: 15, color: 'text.disabled' }} />
          ) : (
            <FiberManualRecordIcon sx={{ fontSize: 7, color: 'text.disabled' }} />
          )}
        </Box>
        <Typography variant="body2" fontWeight={opts.child ? 'normal' : 'medium'} noWrap sx={{ flex: 1, minWidth: 0 }}>
          {cameraLabel(ep.cameraId)}{ep.streamName ? ` / ${ep.streamName}` : ''}
        </Typography>
        {eventSource(ep) !== 'other' && (
          <Chip
            label={eventSource(ep) === 'analytics' ? t('events.sourceAnalytics') : t('events.sourceOnvif')}
            size="small"
            variant="outlined"
            color={eventSource(ep) === 'analytics' ? 'primary' : 'default'}
            sx={{ height: 18, '& .MuiChip-label': { px: 0.75, fontSize: '0.65rem' } }}
          />
        )}
        {opts.badge && opts.badge > 0 ? (
          <Chip
            label={`+${opts.badge}`}
            size="small"
            color="secondary"
            sx={{ height: 18, '& .MuiChip-label': { px: 0.75, fontSize: '0.65rem' } }}
          />
        ) : null}
      </Stack>
      <Typography variant="caption" color="text.secondary" component="div" sx={{ pl: 2.75 }}>
        {new Date(ep.alarmStartTs).toLocaleString()}
      </Typography>
      <Typography variant="caption" color="text.secondary" component="div" sx={{ pl: 2.75 }}>
        {ep.alarmEndTs ? new Date(ep.alarmEndTs).toLocaleTimeString() : t('events.active')}
      </Typography>
    </ListItemButton>
  )

  return (
    <Card variant="outlined">
      <CardContent sx={{ p: 2 }}>
        <Stack spacing={2}>
          <Typography variant="h6">{t('events.title')}</Typography>
          {loading && groups.length === 0 && <LinearProgress />}
          {error && (
            <Typography color="error">
              {error}{' '}
              <Button size="small" onClick={() => void resetAndLoad()}>
                {t('events.retry')}
              </Button>
            </Typography>
          )}

          <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} alignItems="flex-start">
            {/* Left: grouped event tree */}
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
                    <MenuItem key={id} value={id}>{camerasMap[id]?.name || id}</MenuItem>
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
                  {filteredGroups.map((g) => {
                    const rep = g.episodes[0]
                    if (!rep) return null
                    const extra = g.episodes.length - 1
                    const isOpen = expanded.has(g.eventId)
                    return (
                      <Box key={g.eventId}>
                        {renderEpisode(rep, {
                          badge: extra,
                          onToggle: extra > 0 ? () => toggleExpanded(g.eventId) : undefined,
                          isOpen,
                        })}
                        {isOpen && extra > 0 &&
                          g.episodes.slice(1).map((ep) => renderEpisode(ep, { child: true }))}
                      </Box>
                    )
                  })}
                </List>
                {filteredGroups.length === 0 && !loading && (
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
                {loading && groups.length > 0 && <LinearProgress sx={{ flex: 1, minWidth: 80 }} />}
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
