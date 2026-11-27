import { useEffect, useMemo, useRef, useState } from 'react'
import {
  Accordion, AccordionDetails, AccordionSummary, Alert, Autocomplete, Box, Button, Card,
  CardContent, Chip, Collapse, FormControl, FormControlLabel, IconButton, InputLabel,
  LinearProgress, List, ListItemButton, MenuItem, Select, Stack, Switch, TextField,
  ToggleButton, ToggleButtonGroup, Tooltip, Typography,
} from '@mui/material'
import ExpandLessIcon from '@mui/icons-material/ExpandLess'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import RefreshIcon from '@mui/icons-material/Refresh'
import InsightsIcon from '@mui/icons-material/Insights'
import NotificationsActiveIcon from '@mui/icons-material/NotificationsActive'
import SensorsIcon from '@mui/icons-material/Sensors'
import ToggleOnIcon from '@mui/icons-material/ToggleOn'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import type { TFunction } from 'i18next'
import type { MapAlarm, MapEvent, MapMarker } from '../../dto/mapDtos'
import { useAppDispatch, useAppSelector } from '../../store'
import { loadNodes } from '../../store/objectsSlice'
import { eventProp, loadEvents } from '../../store/eventsSlice'
import { useMapFileImage } from '../../hooks/useMapFileImage'
import { mapApi } from '../../api/mapApi'
import { useJournalDeepLink } from '../settings/useJournalDeepLink'
import {
  cameraNodesOf, journalLinkFilter, localCursorValue, lookupLinkedEvent, type EventLookup,
} from './journalLink'
import { AlarmActionButton } from '../alarms/AlarmAction'
import ArchiveHlsPreview from '../ArchiveHlsPreview'
import EventDetailsPanel from '../EventDetailsPanel'
import EventOpenInPlayback from '../EventOpenInPlayback'
import { eventsFilterFromState } from './eventsFilter'
import {
  episodeFromEvent, eventKind, humanProps, isAlarmEvent, paramLabelKey, rowSummary, technicalProps,
} from './eventPresentation'
import type { ArchiveSelectionState, HumanField, SummaryToken } from './eventPresentation'

const AUTO_REFRESH_MS = 15_000

/** The kinds the journal can filter to; 'all' is the absence of the filter. */
type KindFilter = 'all' | 'sensor' | 'relay' | 'analytics' | 'alarm'

const KIND_ICONS: Record<Exclude<KindFilter, 'all'>, React.ReactNode> = {
  sensor: <SensorsIcon fontSize="small" />,
  relay: <ToggleOnIcon fontSize="small" />,
  analytics: <InsightsIcon fontSize="small" />,
  alarm: <NotificationsActiveIcon fontSize="small" />,
}

const imagesOf = (event: MapEvent): string[] =>
  (event.extraProps ?? [])
    .filter((prop) => prop.visualType === 'image_fs' && prop.strVal)
    .map((prop) => prop.strVal as string)

const clock = (iso?: string): string => {
  if (!iso) return ''
  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString()
}

/**
 * Operator → Events: the map's journal, not the camera list. The first filter row is the one the
 * operator reaches for — period, type, object or camera, search — while the protocol's own param
 * fields and "with pictures" wait behind More filters. The list pages by keyset backwards and
 * refreshes itself until the operator asks for older pages.
 */
export default function EventsJournal() {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const navigate = useNavigate()
  const link = useJournalDeepLink()
  const [searchParams] = useSearchParams()
  const subtree = searchParams.get('node') ?? ''

  const applied = useAppSelector((s) => s.events.filter)
  const rows = useAppSelector((s) => s.events.rows)
  const hasMore = useAppSelector((s) => s.events.hasMore)
  const loading = useAppSelector((s) => s.events.loading)
  const error = useAppSelector((s) => s.events.error)
  const markers = useAppSelector((s) => s.objects.markers)
  const objectsBusy = useAppSelector((s) => s.objects.busy)
  const camerasById = useAppSelector((s) => s.live.cameraStatesById)

  const [start, setStart] = useState('')
  // The cursor the wall sent is the page's "To": shown in local time, the way the operator reads
  // the clock on the wall.
  const [end, setEnd] = useState(() => localCursorValue(link.before))
  const [param0, setParam0] = useState('')
  const [param1, setParam1] = useState('')
  const [imagesOnly, setImagesOnly] = useState(false)
  const [text, setText] = useState('')
  const [gallery, setGallery] = useState(false)
  const [autoRefresh, setAutoRefresh] = useState(true)
  const [paged, setPaged] = useState(false)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [kind, setKind] = useState<KindFilter>('all')
  const [objectNode, setObjectNode] = useState<MapMarker | null>(null)
  const [moreFilters, setMoreFilters] = useState(false)

  // A wall link names a camera and a cursor, and sometimes the event it was clicked on. The
  // camera's nodes are resolved once; null means that lookup is still running.
  const [linkCameraNodes, setLinkCameraNodes] = useState<string[] | null>(
    () => (link.cameraId && !subtree ? null : []),
  )
  const [cameraHasNoNodes, setCameraHasNoNodes] = useState(false)
  const [eventLookup, setEventLookup] = useState<EventLookup>(link.eventUuid ? 'pending' : 'found')
  const linkedRowRef = useRef<HTMLElement | null>(null)
  const scrolledToLink = useRef(false)

  // The camera of the wall link becomes the node filter. An unresolved camera is treated like one
  // the map does not carry: the cursor alone must not be passed off as the camera's own page, and
  // the explanation below says as much.
  useEffect(() => {
    const cameraId = link.cameraId
    if (!cameraId || linkCameraNodes !== null) return

    let cancelled = false
    void mapApi.searchDeviceNodes(cameraId)
      .then((response) => {
        if (cancelled) return
        const ids = cameraNodesOf(response.markers ?? [], cameraId)
        setCameraHasNoNodes(ids.length === 0)
        setLinkCameraNodes(ids)
      })
      .catch(() => {
        if (cancelled) return
        setCameraHasNoNodes(true)
        setLinkCameraNodes([])
      })

    return () => { cancelled = true }
  }, [link.cameraId, linkCameraNodes])

  // Loads the page the link asks for, then walks back through older pages until the event it
  // named appears, the journal runs out, or the page cap is reached; then says it was not found
  // instead of showing the newest row as though it were the one asked for.
  useEffect(() => {
    if (linkCameraNodes === null) return

    const eventUuid = link.eventUuid
    const filter = journalLinkFilter({
      node: subtree || null,
      before: link.before,
      cameraNodeIds: linkCameraNodes,
    })
    let cancelled = false

    void (async () => {
      let page
      try {
        page = await dispatch(loadEvents({ filter })).unwrap()
      } catch {
        // The failure is already on the page; the link simply has nothing to select.
        return
      }
      if (cancelled || !eventUuid) return

      let loaded = page.events
      let more = loaded.length >= filter.count
      let fetched = 0

      for (;;) {
        const lookup = lookupLinkedEvent(loaded, eventUuid, more, fetched)
        if (lookup === 'found') {
          if (!cancelled) {
            setEventLookup('found')
            setSelectedId(eventUuid)
          }
          return
        }
        if (lookup === 'notFound') {
          if (!cancelled) setEventLookup('notFound')
          return
        }

        fetched += 1
        // Paging back stops the refresh loop, exactly as the "Load older" button does.
        setPaged(true)
        try {
          page = await dispatch(loadEvents({ filter, append: true })).unwrap()
        } catch {
          return
        }
        if (cancelled) return

        loaded = [...loaded, ...page.events]
        more = page.events.length >= filter.count
      }
    })()

    return () => { cancelled = true }
  }, [subtree, link.before, link.eventUuid, linkCameraNodes, dispatch])

  // Bring the linked row into view once, without fighting the operator's own scrolling later.
  useEffect(() => {
    if (eventLookup !== 'found' || selectedId !== link.eventUuid || scrolledToLink.current) return
    const row = linkedRowRef.current
    if (!row) return
    row.scrollIntoView({ block: 'center' })
    scrolledToLink.current = true
  }, [eventLookup, selectedId, link.eventUuid, rows])

  // Choosing a row is the operator taking the page over from the link, note included.
  const selectRow = (id: string | null) => {
    setSelectedId(id)
    setEventLookup('found')
  }

  // The names of the nodes an event mentions, for the rows and for the archive link.
  const objectIdsKey = useMemo(() => {
    const ids = rows.map((event) => event.objectId).filter((id): id is string => !!id)
    return [...new Set(ids)].sort().join(',')
  }, [rows])

  useEffect(() => {
    const ids = objectIdsKey ? objectIdsKey.split(',') : []
    const missing = ids.filter((id) => !markers[id])
    if (missing.length > 0) void dispatch(loadNodes(missing))
  }, [objectIdsKey, markers, dispatch])

  // Keeps the newest page fresh; a journal the operator has paged back through stays where it is,
  // and so does one still looking for the event a link named.
  useEffect(() => {
    if (!autoRefresh || paged || (link.eventUuid && eventLookup === 'pending')) return
    const timer = window.setInterval(() => { void dispatch(loadEvents({ filter: applied })) }, AUTO_REFRESH_MS)
    return () => window.clearInterval(timer)
  }, [autoRefresh, paged, link.eventUuid, eventLookup, applied, dispatch])

  const apply = () => {
    const filter = eventsFilterFromState({
      start,
      end,
      subtree,
      objectId: objectNode?.id,
      imagesOnly,
      param0,
      param1,
    })

    setPaged(false)
    setSelectedId(null)
    // Applying a filter of one's own ends the link lookup; its note no longer describes the page.
    setEventLookup('found')
    void dispatch(loadEvents({ filter }))
  }

  const refresh = () => void dispatch(loadEvents({ filter: applied }))

  const nameOf = (objectId?: string): string => {
    if (!objectId) return ''
    const marker = markers[objectId]
    const cameraName = marker?.cameraId ? camerasById[marker.cameraId]?.name : undefined
    return marker?.name || cameraName || objectId
  }

  const visibleRows = useMemo(() => {
    const byKind = kind === 'all' ? rows : rows.filter((event) => eventKind(event) === kind)
    if (!text) return byKind

    const needle = text.toLowerCase()
    return byKind.filter((event) => {
      const marker = event.objectId ? markers[event.objectId] : undefined
      const cameraName = marker?.cameraId ? camerasById[marker.cameraId]?.name : undefined
      const haystack = [
        event.id, event.objectId, event.eventName, event.param0, event.param1,
        marker?.name, cameraName,
      ]
      return haystack.some((value) => value?.toLowerCase().includes(needle))
    })
  }, [rows, kind, text, markers, camerasById])

  // While the link's event is being looked for, or was not found, no row is passed off as it:
  // the detail stays empty until the operator picks one.
  const linkSelectionPending = Boolean(link.eventUuid) && eventLookup !== 'found'
  const selected = visibleRows.find((event) => event.id === selectedId)
    ?? (linkSelectionPending ? null : visibleRows[0] ?? null)

  const selectedEpisode = useMemo(() => {
    const cameraId = selected?.objectId ? markers[selected.objectId]?.cameraId : undefined
    return selected ? episodeFromEvent(selected, cameraId) : null
  }, [selected, markers])

  // A picked row whose marker is still on its way says nothing yet; one that has no camera behind
  // it says so, rather than prentending no row was selected.
  const selectionState: ArchiveSelectionState = !selected
    ? 'none'
    : selectedEpisode
      ? 'ready'
      : selected.objectId && !markers[selected.objectId] && objectsBusy > 0
        ? 'pending'
        : 'noArchive'

  const subtreeName = subtree ? nameOf(subtree) : ''
  const param0Label = kind === 'all' ? t('events.param0') : t(paramLabelKey(kind, 0) ?? 'events.param0')
  const param1Label = kind === 'all' ? t('events.param1') : t(paramLabelKey(kind, 1) ?? 'events.param1')

  return (
    <Card variant="outlined" sx={{ height: '100%', display: 'flex', flexDirection: 'column' }}>
      <CardContent sx={{ p: 2, display: 'flex', flexDirection: 'column', minHeight: 0, flex: 1 }}>
        <Stack direction="row" spacing={1} alignItems="center" sx={{ mb: 1, flexShrink: 0 }}>
          <Typography variant="h6" sx={{ flex: 1 }}>{t('events.title')}</Typography>
          {subtree ? (
            <Chip label={t('events.subtree', { name: subtreeName })} size="small" variant="outlined" />
          ) : null}
        </Stack>

        <Stack
          direction={{ xs: 'column', md: 'row' }}
          spacing={1}
          alignItems={{ xs: 'stretch', md: 'center' }}
          useFlexGap
          flexWrap="wrap"
          sx={{ mb: 1, flexShrink: 0 }}
        >
          <TextField
            size="small" type="datetime-local" label={t('events.periodFrom')}
            value={start} onChange={(event) => setStart(event.target.value)}
            InputLabelProps={{ shrink: true }}
          />
          <TextField
            size="small" type="datetime-local" label={t('events.periodTo')}
            value={end} onChange={(event) => setEnd(event.target.value)}
            InputLabelProps={{ shrink: true }}
          />
          <FormControl size="small" sx={{ minWidth: 160 }}>
            <InputLabel id="events-kind-label">{t('events.type')}</InputLabel>
            <Select
              labelId="events-kind-label"
              label={t('events.type')}
              value={kind}
              onChange={(event) => setKind(event.target.value as KindFilter)}
            >
              <MenuItem value="all">{t('events.kind.all')}</MenuItem>
              {(Object.keys(KIND_ICONS) as Exclude<KindFilter, 'all'>[]).map((value) => (
                <MenuItem key={value} value={value}>
                  <Stack direction="row" spacing={0.5} alignItems="center">
                    {KIND_ICONS[value]}
                    {t(`events.kind.${value}`)}
                  </Stack>
                </MenuItem>
              ))}
            </Select>
          </FormControl>
          {!subtree && <ObjectPicker value={objectNode} onChange={setObjectNode} />}
          <TextField
            size="small" label={t('events.searchText')} value={text}
            onChange={(event) => setText(event.target.value)} sx={{ flex: 1, minWidth: 160 }}
          />
          <Button variant="contained" size="small" onClick={apply}>{t('events.apply')}</Button>
          <Button
            size="small"
            onClick={() => setMoreFilters((open) => !open)}
            endIcon={moreFilters ? <ExpandLessIcon /> : <ExpandMoreIcon />}
          >
            {moreFilters ? t('events.lessFilters') : t('events.moreFilters')}
          </Button>
        </Stack>

        <Collapse in={moreFilters} sx={{ flexShrink: 0 }}>
          <Stack
            direction={{ xs: 'column', md: 'row' }}
            spacing={1}
            alignItems={{ xs: 'stretch', md: 'center' }}
            useFlexGap
            flexWrap="wrap"
            sx={{ mb: 1 }}
          >
            <TextField
              size="small" label={param0Label} value={param0}
              onChange={(event) => setParam0(event.target.value)} sx={{ width: 150 }}
            />
            <TextField
              size="small" label={param1Label} value={param1}
              onChange={(event) => setParam1(event.target.value)} sx={{ width: 150 }}
            />
            <FormControlLabel
              control={<Switch size="small" checked={imagesOnly} onChange={(event) => setImagesOnly(event.target.checked)} />}
              label={<Typography variant="caption">{t('events.imagesOnly')}</Typography>}
            />
          </Stack>
        </Collapse>

        {cameraHasNoNodes ? (
          <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
            {t('events.cameraHasNoNodes')}
          </Typography>
        ) : null}
        {(loading && rows.length === 0) || linkCameraNodes === null
          ? <LinearProgress sx={{ flexShrink: 0 }} />
          : null}
        {error ? <Typography color="error" sx={{ mb: 1 }}>{error}</Typography> : null}

        <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ flex: 1, minHeight: 0 }}>
          <Box sx={{ width: { xs: '100%', md: 360 }, flexShrink: 0, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
            {eventLookup === 'notFound' ? (
              <Alert severity="warning" sx={{ mb: 1, flexShrink: 0 }}>{t('events.eventNotFound')}</Alert>
            ) : null}
            {/* The list's own controls sit beside the list: the count and the shape it is shown
                in, with refresh next to them. */}
            <Stack direction="row" spacing={1} alignItems="center" sx={{ mb: 0.5, flexShrink: 0 }}>
              <Typography variant="caption" color="text.secondary">
                {t('events.count', { count: visibleRows.length })}
              </Typography>
              <Box sx={{ flex: 1 }} />
              <ToggleButtonGroup
                exclusive
                size="small"
                value={gallery ? 'gallery' : 'list'}
                onChange={(_, value: string | null) => value && setGallery(value === 'gallery')}
              >
                <ToggleButton value="list">{t('events.list')}</ToggleButton>
                <ToggleButton value="gallery">{t('events.gallery')}</ToggleButton>
              </ToggleButtonGroup>
              <Tooltip title={t('events.refresh')}>
                <IconButton size="small" aria-label={t('events.refresh')} onClick={refresh}>
                  <RefreshIcon fontSize="small" />
                </IconButton>
              </Tooltip>
            </Stack>
            <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', border: 1, borderColor: 'divider', borderRadius: 1 }}>
              {gallery ? (
                <Box sx={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))', gap: 1, p: 1 }}>
                  {visibleRows.map((event) => {
                    const image = imagesOf(event)[0]
                    return (
                      <Box
                        key={event.id ?? event.timestamp}
                        ref={(element) => {
                          if (event.id === link.eventUuid && element instanceof HTMLElement)
                            linkedRowRef.current = element
                        }}
                        onClick={() => selectRow(event.id ?? null)}
                        sx={{
                          border: 1, borderColor: selected?.id === event.id ? 'primary.main' : 'divider',
                          borderRadius: 1, overflow: 'hidden', cursor: 'pointer',
                        }}
                      >
                        <EventImage fileId={image} height={90} />
                        <Typography variant="caption" noWrap sx={{ display: 'block', px: 0.5 }}>
                          {nameOf(event.objectId)} · {clock(event.timestamp)}
                        </Typography>
                      </Box>
                    )
                  })}
                </Box>
              ) : (
                <List dense disablePadding>
                  {visibleRows.map((event) => (
                    <ListItemButton
                      key={event.id ?? event.timestamp}
                      ref={(element) => {
                        if (event.id === link.eventUuid && element instanceof HTMLElement)
                          linkedRowRef.current = element
                      }}
                      selected={selected?.id === event.id}
                      onClick={() => selectRow(event.id ?? null)}
                      sx={{ borderBottom: 1, borderColor: 'divider', display: 'block' }}
                    >
                      <Stack direction="row" spacing={0.5} alignItems="center">
                        <EventKindIcon event={event} />
                        <Typography variant="body2" fontWeight="medium" noWrap sx={{ flex: 1, minWidth: 0 }}>
                          {nameOf(event.objectId)}
                        </Typography>
                        <Typography variant="caption" color="text.secondary">{clock(event.timestamp)}</Typography>
                      </Stack>
                      <Stack direction="row" spacing={0.5} alignItems="center">
                        <Typography variant="caption" color="text.secondary" noWrap sx={{ flex: 1 }}>
                          {summaryText(event, t)}
                        </Typography>
                        {imagesOf(event).length > 0 ? <EventImage fileId={imagesOf(event)[0]} height={28} /> : null}
                      </Stack>
                    </ListItemButton>
                  ))}
                </List>
              )}
              {visibleRows.length === 0 && !loading ? (
                <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>{t('events.noRows')}</Typography>
              ) : null}
            </Box>

            <Stack direction="row" spacing={1} alignItems="center" sx={{ mt: 1, flexShrink: 0 }}>
              <Button size="small" variant="outlined" disabled={!hasMore || loading} onClick={() => {
                setPaged(true)
                void dispatch(loadEvents({ filter: applied, append: true }))
              }}>
                {t('events.loadOlder')}
              </Button>
              <Box sx={{ flex: 1 }} />
              <FormControlLabel
                control={<Switch size="small" checked={autoRefresh} onChange={(event) => setAutoRefresh(event.target.checked)} />}
                label={<Typography variant="caption">{t('events.autoRefresh')}</Typography>}
              />
            </Stack>
          </Box>

          <Box sx={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', minHeight: 0, overflowY: 'auto' }}>
            <Stack direction="row" alignItems="center" spacing={1} sx={{ mb: 1 }}>
              <Typography variant="subtitle1" sx={{ flex: 1 }} noWrap>
                {selected ? nameOf(selected.objectId) : t('events.noSelection')}
              </Typography>
              {selected && isAlarmEvent(selected) && selected.objectId ? (
                <Button
                  size="small"
                  variant="outlined"
                  startIcon={<NotificationsActiveIcon />}
                  onClick={() => navigate(`/operator/map?node=${encodeURIComponent(selected.objectId!)}`)}
                >
                  {t('alarms.openOnMap')}
                </Button>
              ) : null}
              <EventOpenInPlayback episode={selectedEpisode} />
            </Stack>
            <ArchiveHlsPreview selected={selectedEpisode} selection={selectionState} />
            {selected ? <MapEventDetails event={selected} /> : null}
            <EventDetailsPanel selected={selectedEpisode} selection={selectionState} />
          </Box>
        </Stack>
      </CardContent>
    </Card>
  )
}

/** One icon per kind, so a mixed page reads at a glance. */
function EventKindIcon({ event }: { event: MapEvent }) {
  const kind = eventKind(event)
  switch (kind) {
    case 'sensor': return <SensorsIcon fontSize="small" color="action" />
    case 'relay': return <ToggleOnIcon fontSize="small" color="action" />
    case 'analytics': return <InsightsIcon fontSize="small" color="action" />
    case 'alarm': return <NotificationsActiveIcon fontSize="small" color="error" />
    default: return null
  }
}

type Translate = TFunction

function summaryText(event: MapEvent, t: Translate): string {
  return rowSummary(event)
    .map((token: SummaryToken) => ('text' in token ? token.text : t(token.key, token.fallback ?? token.key)))
    .join(' · ')
}

/**
 * Search is server-side and two characters long at least: a lazy tree cannot find a token inside
 * an unopened branch, and a client-side filter would only search what this page happened to load.
 */
function ObjectPicker({ value, onChange }: { value: MapMarker | null; onChange: (marker: MapMarker | null) => void }) {
  const { t } = useTranslation()
  const [input, setInput] = useState('')
  const [options, setOptions] = useState<MapMarker[]>([])
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    const query = input.trim()
    if (query.length < 2) return

    let cancelled = false
    const timer = window.setTimeout(() => {
      if (cancelled) return
      setLoading(true)
      void mapApi.searchDeviceNodes(query, 20)
        .then((response) => { if (!cancelled) setOptions(response.markers ?? []) })
        .catch(() => { if (!cancelled) setOptions([]) })
        .finally(() => { if (!cancelled) setLoading(false) })
    }, 300)

    return () => {
      cancelled = true
      window.clearTimeout(timer)
    }
  }, [input])

  return (
    <Autocomplete
      size="small"
      sx={{ width: 220 }}
      options={options}
      value={value}
      loading={loading}
      // The server already matched the query; filtering again on the label would hide a node whose
      // name differs from the token that found it.
      filterOptions={(found) => found}
      getOptionLabel={(option) => option.name || option.id}
      isOptionEqualToValue={(option, picked) => option.id === picked.id}
      onChange={(_event, picked) => onChange(picked)}
      onInputChange={(_event, next) => {
        setInput(next)
        // Clearing here rather than in the effect keeps the effect free of synchronous state
        // writes; a query that is too short simply has nothing to show.
        if (next.trim().length < 2) setOptions([])
      }}
      renderInput={(params) => <TextField {...params} label={t('events.objectOrCamera')} />}
    />
  )
}

function EventImage({ fileId, height }: { fileId?: string; height: number }) {
  const url = useMapFileImage(fileId)

  if (!fileId) return null
  if (!url) {
    return <Box sx={{ height, width: height, bgcolor: 'action.hover', borderRadius: 0.5, flexShrink: 0 }} />
  }

  return (
    <Box
      component="img"
      src={url}
      alt=""
      sx={{ height, maxWidth: 160, objectFit: 'contain', borderRadius: 0.5, flexShrink: 0, bgcolor: '#000' }}
    />
  )
}

function shownValue(field: HumanField, t: Translate): string {
  if (field.format === 'percent') {
    const ratio = Number(field.value)
    return Number.isFinite(ratio) ? `${Math.round(ratio * 100)}%` : field.value
  }
  if (field.format === 'time') return clock(field.value)
  return field.valueKey ? t(field.valueKey, field.value) : field.value
}

/** The event's own fields and properties, beside the archive picture. */
function MapEventDetails({ event }: { event: MapEvent }) {
  const { t } = useTranslation()
  const images = imagesOf(event)
  // An alarm lifecycle event names the row it belongs to; the plain retention `alarm` event does
  // not, and has no separate row to accept.
  const alarmId = eventProp(event, 'alarm_id')
  const human = humanProps(event)
  const technical = technicalProps(event)

  return (
    <Box sx={{ my: 1.5 }}>
      <Typography variant="overline" color="text.secondary">{t('events.properties')}</Typography>
      <Stack spacing={0.25} sx={{ mb: 1 }}>
        {human.map((field, index) => (
          <PropertyRow key={`${field.labelKey}-${index}`} name={t(field.labelKey)} value={shownValue(field, t)} />
        ))}
      </Stack>

      {images.length > 0 ? (
        <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap sx={{ mb: 1 }}>
          {images.map((image) => <EventImage key={image} fileId={image} height={120} />)}
        </Stack>
      ) : null}

      {alarmId ? <EventAlarmAction alarmId={alarmId} /> : null}

      {technical.length > 0 ? (
        <Accordion
          disableGutters
          sx={{ bgcolor: 'transparent', '&:before': { display: 'none' } }}
        >
          <AccordionSummary
            expandIcon={<ExpandMoreIcon fontSize="small" />}
            sx={{ px: 0, minHeight: 0, '& .MuiAccordionSummary-content': { my: 0.5 } }}
          >
            <Typography variant="caption" color="text.secondary">{t('events.technical')}</Typography>
          </AccordionSummary>
          <AccordionDetails sx={{ px: 0, pt: 0 }}>
            <Stack spacing={0.25}>
              {technical.map((field, index) => (
                <PropertyRow
                  key={`${field.name}-${index}`}
                  name={t(field.labelKey ?? field.name, field.name)}
                  value={field.value}
                />
              ))}
            </Stack>
          </AccordionDetails>
        </Accordion>
      ) : null}
    </Box>
  )
}

/**
 * The alarm a lifecycle event names: read once, because the journal holds no alarm page. The
 * control is the same one the map's list and the object panel show.
 */
function EventAlarmAction({ alarmId }: { alarmId: string }) {
  const [alarm, setAlarm] = useState<MapAlarm | null>(null)

  useEffect(() => {
    let cancelled = false
    void mapApi.getAlarm(alarmId)
      .then((row) => { if (!cancelled) setAlarm(row) })
      .catch(() => { if (!cancelled) setAlarm(null) })
    return () => { cancelled = true }
  }, [alarmId])

  if (!alarm || alarm.status === 'closed') return null

  return (
    <Box sx={{ mb: 1.5 }}>
      <AlarmActionButton alarm={alarm} onDone={() => setAlarm(null)} />
    </Box>
  )
}

function PropertyRow({ name, value }: { name?: string; value?: string }) {
  return (
    <Stack direction="row" spacing={1}>
      <Typography variant="caption" color="text.secondary" sx={{ minWidth: 110, flexShrink: 0 }}>{name}</Typography>
      <Typography variant="body2" sx={{ wordBreak: 'break-word' }}>{value && value.length > 0 ? value : '—'}</Typography>
    </Stack>
  )
}
