import { useEffect, useMemo, useState } from 'react'
import {
  Box, Button, Card, CardContent, Chip, FormControlLabel, LinearProgress, List, ListItemButton,
  Stack, Switch, TextField, ToggleButton, ToggleButtonGroup, Typography,
} from '@mui/material'
import RefreshIcon from '@mui/icons-material/Refresh'
import InsightsIcon from '@mui/icons-material/Insights'
import NotificationsActiveIcon from '@mui/icons-material/NotificationsActive'
import SensorsIcon from '@mui/icons-material/Sensors'
import ToggleOnIcon from '@mui/icons-material/ToggleOn'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import { toIsoUtc } from '../../dto/dtos'
import type { EpisodeDto } from '../../dto/dtos'
import type { MapEvent } from '../../dto/mapDtos'
import { useAppDispatch, useAppSelector } from '../../store'
import { loadNodes } from '../../store/objectsSlice'
import {
  DEFAULT_EVENT_COUNT, emptyEventsFilter, eventProp, loadEvents, type EventsFilter,
} from '../../store/eventsSlice'
import { useMapFileImage } from '../../hooks/useMapFileImage'
import ArchiveHlsPreview from '../ArchiveHlsPreview'
import EventDetailsPanel from '../EventDetailsPanel'
import EventOpenInPlayback from '../EventOpenInPlayback'

const AUTO_REFRESH_MS = 15_000

/** The event kinds the journal groups, as the map's own dictionary names them. */
type EventKind = 'all' | 'sensor' | 'relay' | 'analytics' | 'alarm'

const isAlarmEvent = (event: MapEvent): boolean =>
  event.eventName === 'alarm' || (event.eventName ?? '').startsWith('alarm_')

const kindOf = (event: MapEvent): EventKind => {
  if (isAlarmEvent(event)) return 'alarm'
  if (event.eventName === 'sensor' || event.eventName === 'relay' || event.eventName === 'analytics')
    return event.eventName
  return 'all'
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
 * Operator → Events: the map's journal, not the camera list. Events are filtered by a period, the
 * subtree of the node the map sent, param 0/1 and whether they carry a picture; the list pages by
 * keyset backwards and refreshes itself until the operator asks for older pages. A selected event
 * opens its archive and its detection crops, the way the retention journal did.
 */
export default function EventsJournal() {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const navigate = useNavigate()
  const [searchParams] = useSearchParams()
  const subtree = searchParams.get('node') ?? ''

  const applied = useAppSelector((s) => s.events.filter)
  const rows = useAppSelector((s) => s.events.rows)
  const hasMore = useAppSelector((s) => s.events.hasMore)
  const loading = useAppSelector((s) => s.events.loading)
  const error = useAppSelector((s) => s.events.error)
  const markers = useAppSelector((s) => s.objects.markers)
  const camerasById = useAppSelector((s) => s.live.cameraStatesById)

  const [start, setStart] = useState('')
  const [end, setEnd] = useState('')
  const [param0, setParam0] = useState('')
  const [param1, setParam1] = useState('')
  const [imagesOnly, setImagesOnly] = useState(false)
  const [text, setText] = useState('')
  const [gallery, setGallery] = useState(false)
  const [autoRefresh, setAutoRefresh] = useState(true)
  const [paged, setPaged] = useState(false)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [kind, setKind] = useState<EventKind>('all')

  useEffect(() => {
    void dispatch(loadEvents({
      filter: { ...emptyEventsFilter, objects: subtree ? [subtree] : [] },
    }))
  }, [subtree, dispatch])

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

  // Keeps the newest page fresh; a journal the operator has paged back through stays where it is.
  useEffect(() => {
    if (!autoRefresh || paged) return
    const timer = window.setInterval(() => { void dispatch(loadEvents({ filter: applied })) }, AUTO_REFRESH_MS)
    return () => window.clearInterval(timer)
  }, [autoRefresh, paged, applied, dispatch])

  const apply = () => {
    const filter: EventsFilter = {
      timeStart: toIsoUtc(start) ?? undefined,
      timeEnd: toIsoUtc(end) ?? undefined,
      objects: subtree ? [subtree] : [],
      imagesOnly,
      param0,
      param1,
      count: DEFAULT_EVENT_COUNT,
    }

    setPaged(false)
    setSelectedId(null)
    void dispatch(loadEvents({ filter }))
  }

  const nameOf = (objectId?: string): string => {
    if (!objectId) return ''
    const marker = markers[objectId]
    const cameraName = marker?.cameraId ? camerasById[marker.cameraId]?.name : undefined
    return marker?.name || cameraName || objectId
  }

  const visibleRows = useMemo(() => {
    const byKind = kind === 'all' ? rows : rows.filter((event) => kindOf(event) === kind)
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

  const selected = visibleRows.find((event) => event.id === selectedId) ?? visibleRows[0] ?? null

  const selectedEpisode = useMemo<EpisodeDto | null>(() => {
    if (!selected?.objectId) return null
    const cameraId = markers[selected.objectId]?.cameraId
    if (!cameraId) return null

    return {
      eventUuid: selected.id ?? '',
      eventId: selected.id ?? '',
      objectId: selected.objectId,
      cameraId,
      streamName: selected.param0 || undefined,
      alarmStartTs: selected.timestamp ?? new Date(0).toISOString(),
      alarmEndTs: eventProp(selected, 'alarm_end_ts'),
      trigger: selected.param1 || undefined,
    }
  }, [selected, markers])

  const subtreeName = subtree ? nameOf(subtree) : ''

  return (
    <Card variant="outlined" sx={{ height: '100%', display: 'flex', flexDirection: 'column' }}>
      <CardContent sx={{ p: 2, display: 'flex', flexDirection: 'column', minHeight: 0, flex: 1 }}>
        <Stack direction="row" spacing={1} alignItems="center" sx={{ mb: 1, flexShrink: 0 }}>
          <Typography variant="h6" sx={{ flex: 1 }}>{t('events.title')}</Typography>
          {subtree ? (
            <Chip label={t('events.subtree', { name: subtreeName })} size="small" variant="outlined" />
          ) : null}
          <ToggleButtonGroup
            exclusive
            size="small"
            value={kind}
            onChange={(_, value: EventKind | null) => value && setKind(value)}
          >
            <ToggleButton value="all">{t('events.kind.all')}</ToggleButton>
            <ToggleButton value="sensor" title={t('events.kind.sensor')}>
              <SensorsIcon fontSize="small" sx={{ mr: 0.5 }} />{t('events.kind.sensor')}
            </ToggleButton>
            <ToggleButton value="relay" title={t('events.kind.relay')}>
              <ToggleOnIcon fontSize="small" sx={{ mr: 0.5 }} />{t('events.kind.relay')}
            </ToggleButton>
            <ToggleButton value="analytics" title={t('events.kind.analytics')}>
              <InsightsIcon fontSize="small" sx={{ mr: 0.5 }} />{t('events.kind.analytics')}
            </ToggleButton>
            <ToggleButton value="alarm" title={t('events.kind.alarm')}>
              <NotificationsActiveIcon fontSize="small" sx={{ mr: 0.5 }} />{t('events.kind.alarm')}
            </ToggleButton>
          </ToggleButtonGroup>
          <ToggleButtonGroup
            exclusive
            size="small"
            value={gallery ? 'gallery' : 'list'}
            onChange={(_, value: string | null) => value && setGallery(value === 'gallery')}
          >
            <ToggleButton value="list">{t('events.list')}</ToggleButton>
            <ToggleButton value="gallery">{t('events.gallery')}</ToggleButton>
          </ToggleButtonGroup>
        </Stack>

        <Stack direction={{ xs: 'column', md: 'row' }} spacing={1} sx={{ mb: 1, flexShrink: 0 }}>
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
          <TextField
            size="small" label={t('events.param0')} value={param0}
            onChange={(event) => setParam0(event.target.value)} sx={{ width: 130 }}
          />
          <TextField
            size="small" label={t('events.param1')} value={param1}
            onChange={(event) => setParam1(event.target.value)} sx={{ width: 130 }}
          />
          <TextField
            size="small" label={t('events.searchText')} value={text}
            onChange={(event) => setText(event.target.value)} sx={{ flex: 1, minWidth: 120 }}
          />
          <FormControlLabel
            control={<Switch size="small" checked={imagesOnly} onChange={(event) => setImagesOnly(event.target.checked)} />}
            label={<Typography variant="caption">{t('events.imagesOnly')}</Typography>}
          />
          <Button variant="contained" size="small" onClick={apply}>{t('events.apply')}</Button>
          <Button
            variant="outlined" size="small" startIcon={<RefreshIcon />}
            onClick={() => void dispatch(loadEvents({ filter: applied }))}
          >
            {t('events.refresh')}
          </Button>
        </Stack>

        {loading && rows.length === 0 ? <LinearProgress sx={{ flexShrink: 0 }} /> : null}
        {error ? <Typography color="error" sx={{ mb: 1 }}>{error}</Typography> : null}

        <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ flex: 1, minHeight: 0 }}>
          <Box sx={{ width: { xs: '100%', md: 360 }, flexShrink: 0, display: 'flex', flexDirection: 'column', minHeight: 0 }}>
            <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', border: 1, borderColor: 'divider', borderRadius: 1 }}>
              {gallery ? (
                <Box sx={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))', gap: 1, p: 1 }}>
                  {visibleRows.map((event) => {
                    const image = imagesOf(event)[0]
                    return (
                      <Box
                        key={event.id ?? event.timestamp}
                        onClick={() => setSelectedId(event.id ?? null)}
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
                      selected={selected?.id === event.id}
                      onClick={() => setSelectedId(event.id ?? null)}
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
                          {[event.eventName, event.param0, event.param1].filter(Boolean).join(' · ')}
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
            <ArchiveHlsPreview selected={selectedEpisode} />
            {selected ? <MapEventDetails event={selected} /> : null}
            <EventDetailsPanel selected={selectedEpisode} />
          </Box>
        </Stack>
      </CardContent>
    </Card>
  )
}

/** One icon per kind, so a mixed page reads at a glance. */
function EventKindIcon({ event }: { event: MapEvent }) {
  switch (kindOf(event)) {
    case 'sensor': return <SensorsIcon fontSize="small" color="action" />
    case 'relay': return <ToggleOnIcon fontSize="small" color="action" />
    case 'analytics': return <InsightsIcon fontSize="small" color="action" />
    case 'alarm': return <NotificationsActiveIcon fontSize="small" color="error" />
    default: return null
  }
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

/** The event's own fields and properties, beside the archive picture. */
function MapEventDetails({ event }: { event: MapEvent }) {
  const { t } = useTranslation()
  const images = imagesOf(event)
  const props = (event.extraProps ?? []).filter((prop) => prop.visualType !== 'image_fs')

  return (
    <Box sx={{ my: 1.5 }}>
      <Typography variant="overline" color="text.secondary">{t('events.properties')}</Typography>
      <Stack spacing={0.25} sx={{ mb: 1 }}>
        <PropertyRow name={t('events.eventName')} value={event.eventName} />
        <PropertyRow name={t('events.param0')} value={event.param0} />
        <PropertyRow name={t('events.param1')} value={event.param1} />
        <PropertyRow name={t('events.priority')} value={event.eventPriority?.toString()} />
        <PropertyRow name={t('events.time')} value={clock(event.timestamp)} />
        {props.map((prop) => (
          <PropertyRow key={prop.propName} name={prop.propName} value={prop.strVal} />
        ))}
      </Stack>
      {images.length > 0 ? (
        <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap>
          {images.map((image) => <EventImage key={image} fileId={image} height={120} />)}
        </Stack>
      ) : null}
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
