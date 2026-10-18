import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
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
import { useSearchParams } from 'react-router-dom'

import type { EpisodeDto, EventGroupDto, RetentionEventGroupsListDto } from '../dto/dtos'
import { useAppSelector } from '../store'
import { getErrorMessage } from '../store/api'
import ArchiveHlsPreview from './ArchiveHlsPreview'
import EventDetailsPanel, { eventSource } from './EventDetailsPanel'
import EventOpenInPlayback from './EventOpenInPlayback'

const PAGE_SIZE = 20
// The left column's height is fixed rather than capped: with a cap it shrinks to fit the list, and
// the pager and refresh controls under it move up and down as events come and go. Controls stay put
// and the list scrolls inside the space it is given.
const COLUMN_HEIGHT = 'min(75vh, 900px)'
const AUTO_REFRESH_MS = 15_000

// The camera filter goes to the server, not to the rendered list: a page is PAGE_SIZE groups
// picked globally, so filtering it here leaves a near-empty page and paging walks through other
// cameras' events. With the filter applied server-side every page is full of this camera's events.
async function fetchRetentionEventGroupsPage(
  before: string | undefined,
  cameraId: string
): Promise<RetentionEventGroupsListDto> {
  const qs = new URLSearchParams()
  qs.set('limit', String(PAGE_SIZE))
  if (before) qs.set('before', before)
  if (cameraId) qs.set('cameraId', cameraId)
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
  // Set when arriving from an event bracket on the timeline: which camera, which page, which row.
  const [searchParams] = useSearchParams()
  const wantedUuid = searchParams.get('eventUuid')

  const [groups, setGroups] = useState<EventGroupDto[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [page, setPage] = useState(0)
  // cursors[i] is the `before` value that fetches page i (cursors[0] === undefined = newest page).
  const [cursors, setCursors] = useState<(string | undefined)[]>([searchParams.get('before') ?? undefined])
  const [hasMore, setHasMore] = useState(false)
  // Off when arriving from a timeline bracket: the operator came to look at one specific episode
  // on a page anchored in the past, and a refresh every 15s only risks moving the ground under it.
  const [autoRefresh, setAutoRefresh] = useState(!searchParams.get('eventUuid'))
  const [selected, setSelected] = useState<EpisodeDto | null>(null)
  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const [cameraFilter, setCameraFilter] = useState(searchParams.get('cameraId') ?? '')
  const [textFilter, setTextFilter] = useState('')
  const wantedApplied = useRef(false)
  const listRef = useRef<HTMLDivElement>(null)

  const loadPage = useCallback(async (pageIndex: number, cursor: string | undefined) => {
    setLoading(true)
    setError(null)
    try {
      const resp = await fetchRetentionEventGroupsPage(cursor, cameraFilter)
      setGroups(resp.groups)
      setPage(pageIndex)
      setHasMore(resp.hasMore)
      setCursors((prev) => {
        const next = prev.slice(0, pageIndex + 1)
        next[pageIndex] = cursor
        if (resp.hasMore && resp.nextBeforeAlarmStart) next[pageIndex + 1] = resp.nextBeforeAlarmStart
        return next
      })
      // A requested episode wins over "keep what was selected": it is why this page was loaded.
      // Only on the first load that finds it, so the auto-refresh below does not keep dragging the
      // selection back after the operator has clicked elsewhere.
      const wantedGroup = wantedUuid && !wantedApplied.current
        ? resp.groups.find((g) => g.episodes.some((e) => e.eventUuid === wantedUuid))
        : undefined
      const wanted = wantedGroup?.episodes.find((e) => e.eventUuid === wantedUuid)
      if (wantedGroup && wanted) {
        wantedApplied.current = true
        // A group is rendered by its FIRST episode with the rest collapsed underneath. Without
        // expanding it, an episode deeper in the group is selected but invisible, and the group's
        // first episode is the row the operator actually sees highlighted.
        setExpanded((prev) => new Set(prev).add(wantedGroup.eventId))
      }
      setSelected((prev) => wanted ?? prev ?? resp.groups[0]?.episodes[0] ?? null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
      setGroups([])
      setHasMore(false)
    } finally {
      setLoading(false)
    }
  }, [wantedUuid, cameraFilter])

  // Loads page 0: on mount at the cursor a timeline bracket asked for, and again whenever the
  // camera filter changes — a different filter is a different result set, so the collected page
  // cursors no longer mean anything.
  //
  // Whether the link's cursor still applies is decided by comparing the filter, never by counting
  // runs: under StrictMode this effect fires twice on mount, and a "use it once then clear it"
  // flag would spend the cursor on the first run and load the newest page on the second — landing
  // the operator on the latest event instead of the one they clicked. The latch below only ever
  // goes from false to true, so running it twice changes nothing.
  const linkCursor = searchParams.get('before') ?? undefined
  const initialFilter = useRef(cameraFilter)
  const filterChanged = useRef(false)
  useEffect(() => {
    if (cameraFilter !== initialFilter.current) filterChanged.current = true
    void loadPage(0, filterChanged.current ? undefined : linkCursor)
    // linkCursor is fixed for the life of this route; loadPage changes with the filter.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [loadPage])

  // Bring the requested row into view. The list scrolls internally, so it is found by attribute
  // inside the scroller rather than by holding a ref to a row that may not be mounted yet.
  useEffect(() => {
    if (!wantedUuid || selected?.eventUuid !== wantedUuid) return
    listRef.current
      ?.querySelector(`[data-event-uuid="${wantedUuid}"]`)
      ?.scrollIntoView({ block: 'nearest' })
  }, [wantedUuid, selected, groups, expanded])

  // Optional auto-refresh: re-fetch the CURRENT page (non-disruptive — keeps your place and selection)
  // rather than snapping back to the newest page.
  useEffect(() => {
    if (!autoRefresh) return
    const id = setInterval(() => void loadPage(page, cursors[page]), AUTO_REFRESH_MS)
    return () => clearInterval(id)
  }, [autoRefresh, page, cursors, loadPage])

  const cameraLabel = (id: string) => camerasMap[id]?.name || id

  // Camera filtering happens server-side (see fetchRetentionEventGroupsPage); only the free-text
  // filter narrows the fetched page here.
  const filteredGroups = useMemo(() => groups.filter((g) => {
    if (textFilter) {
      const q = textFilter.toLowerCase()
      const hit = g.eventId.toLowerCase().includes(q)
        || (g.objectId ?? '').toLowerCase().includes(q)
        || g.episodes.some((e) => e.cameraId.toLowerCase().includes(q) || (e.streamName ?? '').toLowerCase().includes(q))
      if (!hit) return false
    }
    return true
  }), [groups, textFilter])

  const toggleExpanded = (eventId: string) => setExpanded((prev) => {
    const next = new Set(prev)
    if (next.has(eventId)) next.delete(eventId)
    else next.add(eventId)
    return next
  })

  const renderEpisode = (ep: EpisodeDto, opts: { child?: boolean; badge?: number; onToggle?: () => void; isOpen?: boolean }) => (
    <ListItemButton
      key={ep.eventUuid}
      data-event-uuid={ep.eventUuid}
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
              height: { xs: 'auto', md: COLUMN_HEIGHT }
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
                  height: { xs: 320, md: 'auto' },
                  overflow: 'auto'
                }}
                ref={listRef}
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
              <Stack direction="row" justifyContent="flex-end" sx={{ mb: 1 }}>
                <EventOpenInPlayback episode={selected} />
              </Stack>
              <ArchiveHlsPreview selected={selected} />
            </Box>

            {/* Right: extended details */}
            <Box
              sx={{
                width: { xs: '100%', md: 320 },
                flexShrink: 0,
                maxHeight: { xs: 'none', md: COLUMN_HEIGHT },
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
