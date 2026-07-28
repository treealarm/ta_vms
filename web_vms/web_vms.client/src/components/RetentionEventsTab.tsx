import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  FormControlLabel,
  IconButton,
  LinearProgress,
  List,
  ListItemButton,
  MenuItem,
  Stack,
  Switch,
  TextField,
  Typography
} from '@mui/material'
import ChevronLeftIcon from '@mui/icons-material/ChevronLeft'
import ChevronRightIcon from '@mui/icons-material/ChevronRight'
import FirstPageIcon from '@mui/icons-material/FirstPage'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import FiberManualRecordIcon from '@mui/icons-material/FiberManualRecord'
import SubdirectoryArrowRightIcon from '@mui/icons-material/SubdirectoryArrowRight'
import { useTranslation } from 'react-i18next'

import type { EpisodeDto, EventGroupDto, RetentionEventGroupsListDto } from '../dto/dtos'
import { useAppSelector } from '../store'
import { getErrorMessage } from '../store/api'
import ArchiveHlsPreview from './ArchiveHlsPreview'
import EventDetailsPanel, { eventSource } from './EventDetailsPanel'

const PAGE_SIZE = 20
const COLUMN_MAX_HEIGHT = 'min(75vh, 900px)'
const AUTO_REFRESH_MS = 15_000

async function fetchRetentionEventGroupsPage(before: string | undefined): Promise<RetentionEventGroupsListDto> {
  const qs = new URLSearchParams()
  qs.set('limit', String(PAGE_SIZE))
  if (before) qs.set('before', before)
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
  const [page, setPage] = useState(0)
  // cursors[i] is the `before` value that fetches page i (cursors[0] === undefined = newest page).
  const [cursors, setCursors] = useState<(string | undefined)[]>([undefined])
  const [hasMore, setHasMore] = useState(false)
  const [autoRefresh, setAutoRefresh] = useState(true)
  const [selected, setSelected] = useState<EpisodeDto | null>(null)
  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const [cameraFilter, setCameraFilter] = useState('')
  const [textFilter, setTextFilter] = useState('')

  const loadPage = useCallback(async (pageIndex: number, cursor: string | undefined) => {
    setLoading(true)
    setError(null)
    try {
      const resp = await fetchRetentionEventGroupsPage(cursor)
      setGroups(resp.groups)
      setPage(pageIndex)
      setHasMore(resp.hasMore)
      setCursors((prev) => {
        const next = prev.slice(0, pageIndex + 1)
        next[pageIndex] = cursor
        if (resp.hasMore && resp.nextBeforeAlarmStart) next[pageIndex + 1] = resp.nextBeforeAlarmStart
        return next
      })
      setSelected((prev) => prev ?? resp.groups[0]?.episodes[0] ?? null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
      setGroups([])
      setHasMore(false)
    } finally {
      setLoading(false)
    }
  }, [])

  // Initial load (newest page).
  useEffect(() => { void loadPage(0, undefined) }, [loadPage])

  // Optional auto-refresh: re-fetch the CURRENT page (non-disruptive — keeps your place and selection)
  // rather than snapping back to the newest page.
  useEffect(() => {
    if (!autoRefresh) return
    const id = setInterval(() => void loadPage(page, cursors[page]), AUTO_REFRESH_MS)
    return () => clearInterval(id)
  }, [autoRefresh, page, cursors, loadPage])

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
              <Button size="small" onClick={() => void loadPage(page, cursors[page])}>
                {t('events.retry')}
              </Button>
            </Typography>
          )}

          <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} alignItems="flex-start">
            {/* Left: grouped event list, paged. Flex column bounded to the panel height so the list
                scrolls internally and the pager/refresh controls stay pinned in view below it. */}
            <Box sx={{
              width: { xs: '100%', md: 260 }, flexShrink: 0,
              display: 'flex', flexDirection: 'column',
              maxHeight: { md: COLUMN_MAX_HEIGHT }
            }}>
              <Stack direction="row" spacing={1} sx={{ mb: 1, flexShrink: 0 }}>
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
                  border: 1,
                  borderColor: 'divider',
                  borderRadius: 1,
                  // Take the remaining column height and scroll internally (minHeight:0 lets the flex
                  // child shrink); mobile keeps a fixed cap.
                  flex: 1,
                  minHeight: 0,
                  maxHeight: { xs: 320, md: 'none' },
                  overflow: 'auto'
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

              {/* Pager */}
              <Stack direction="row" spacing={0.5} alignItems="center" sx={{ mt: 1, flexShrink: 0 }}>
                <Button variant="outlined" size="small" disabled={loading} onClick={() => void loadPage(page, cursors[page])}>
                  {t('events.refresh')}
                </Button>
                <Box sx={{ flex: 1 }} />
                <IconButton size="small" disabled={loading || page === 0} onClick={() => void loadPage(0, undefined)}>
                  <FirstPageIcon fontSize="small" />
                </IconButton>
                <IconButton size="small" disabled={loading || page === 0} onClick={() => void loadPage(page - 1, cursors[page - 1])}>
                  <ChevronLeftIcon fontSize="small" />
                </IconButton>
                <Typography variant="body2" color="text.secondary">{page + 1}</Typography>
                <IconButton size="small" disabled={loading || !hasMore} onClick={() => void loadPage(page + 1, cursors[page + 1])}>
                  <ChevronRightIcon fontSize="small" />
                </IconButton>
              </Stack>

              <Stack direction="row" alignItems="center" sx={{ mt: 0.5, flexShrink: 0 }}>
                <FormControlLabel
                  control={<Switch size="small" checked={autoRefresh} onChange={(e) => setAutoRefresh(e.target.checked)} />}
                  label={<Typography variant="caption" color="text.secondary">{t('events.autoRefresh', 'Auto-refresh')}</Typography>}
                />
                {loading && groups.length > 0 && <LinearProgress sx={{ flex: 1, minWidth: 60, ml: 1 }} />}
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
