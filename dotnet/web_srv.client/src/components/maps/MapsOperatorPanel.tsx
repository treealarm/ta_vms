import { useEffect, useMemo, useState } from 'react'
import { Box, Tab, Tabs, ToggleButton, Tooltip, Typography } from '@mui/material'
import AccountTreeIcon from '@mui/icons-material/AccountTree'
import NotificationsActiveIcon from '@mui/icons-material/NotificationsActive'
import PersonPinCircleIcon from '@mui/icons-material/PersonPinCircle'
import { Polyline } from 'react-leaflet'
import { useTranslation } from 'react-i18next'
import type { GeoPointDto, PtzGeoAimResultDto } from '../../dto/dtos'
import type { MapEvent, MapMarker } from '../../dto/mapDtos'
import { useAppDispatch, useAppSelector } from '../../store'
import { loadCameras } from '../../store/liveSlice'
import { errorText, loadPlacements, loadSpaces, spatialRequest } from '../../store/spatialSlice'
import { resolveCameraNode } from '../../store/objectsSlice'
import { RECENT_ALARM_MINUTES, alarmEndMs } from '../../store/eventsSlice'
import { useMapSpace } from '../../hooks/useMapSpace'
import { useAlarmFeed } from '../../hooks/useAlarmFeed'
import SpaceCanvas, { type MapFocus } from '../../map/SpaceCanvas'
import PlanOverlay from '../../map/PlanOverlay'
import FiguresLayer from '../../map/FiguresLayer'
import { CameraMarker } from '../../map/markers'
import { Footprints } from '../../map/footprints'
import DetectionsLayer from '../../map/DetectionsLayer'
import { ALARM_COLOR, DEFAULT_NODE_COLOR, nodeStyle } from '../../map/nodeStyle'
import ObjectTree from '../objects/ObjectTree'
import ObjectPanel from '../objects/ObjectPanel'
import SpaceNav from './SpaceNav'
import MapObjectList from './MapObjectList'
import { childrenOf, rootsOf, sharesGeometry } from './spatialUtils'

/** The camera list carries recording state and stream freshness; this is how stale the map may get. */
const CAMERA_POLL_MS = 5000
/** Placements change when an administrator edits the map — rarely. */
const PLACEMENT_POLL_MS = 30000

// Which space the operator last looked at, so the page reopens there. A convenience only: when it is
// missing or gone, the page opens the first root.
const LAST_SPACE_KEY = 'maps.operator.space'
const readLastSpace = () => { try { return localStorage.getItem(LAST_SPACE_KEY) } catch { return null } }
const writeLastSpace = (id: string) => { try { localStorage.setItem(LAST_SPACE_KEY, id) } catch { /* private mode */ } }

// Whether detections are drawn on the map; on unless the operator turned them off.
const DETECTIONS_KEY = 'maps.operator.detections'
const readDetections = () => { try { return localStorage.getItem(DETECTIONS_KEY) !== 'off' } catch { return true } }
const writeDetections = (on: boolean) => { try { localStorage.setItem(DETECTIONS_KEY, on ? 'on' : 'off') } catch { /* private mode */ } }

type AlarmEntry = {
  key: string
  nodeId: string
  cameraId?: string
  name: string
  secondary: string
  active: boolean
}

/**
 * Operator → Map: the one tree of nodes on the left, the space where the chosen node stands in the
 * middle — its arbitrary figures painted by their states — and the node itself on the right.
 * Alarms come from states and the journal, not from a polled camera list.
 */
export default function MapsOperatorPanel() {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const spaces = useAppSelector((s) => s.spatial.spaces)
  const placements = useAppSelector((s) => s.spatial.placements)
  const loaded = useAppSelector((s) => s.spatial.loaded)
  const camerasById = useAppSelector((s) => s.live.cameraStatesById)
  const markers = useAppSelector((s) => s.objects.markers)
  const statesById = useAppSelector((s) => s.states.states)
  const alarmsById = useAppSelector((s) => s.states.alarms)
  const descriptions = useAppSelector((s) => s.states.descriptions)
  const alarmStates = useAppSelector((s) => s.states.all)
  const recentAlarms = useAppSelector((s) => s.events.recentAlarms)
  const levels = useAppSelector((s) => s.levels.levels)

  const [spaceId, setSpaceId] = useState<string | null>(readLastSpace)
  const [selectedNodeId, setSelectedNodeId] = useState<string | null>(null)
  const [checkedIds, setCheckedIds] = useState<Set<string>>(new Set())
  const [leftTab, setLeftTab] = useState(0)
  const [aiming, setAiming] = useState(false)
  const [aimTarget, setAimTarget] = useState<GeoPointDto | null>(null)
  const [aimResult, setAimResult] = useState<PtzGeoAimResultDto | null>(null)
  const [aimError, setAimError] = useState<string | null>(null)
  const [focus, setFocus] = useState<MapFocus | null>(null)
  const [detections, setDetections] = useState(readDetections)

  const space = spaces.find((s) => s.id === spaceId) ?? rootsOf(spaces)[0] ?? null
  const spaceData = useMapSpace(space)
  useAlarmFeed()

  const here = placements
    .filter((p) => p.spaceId === space?.id && camerasById[p.cameraId])
    .sort((a, b) => (camerasById[a.cameraId].name || a.cameraId).localeCompare(camerasById[b.cameraId].name || b.cameraId))
  const nested = space?.id ? childrenOf(spaces, space.id) : []

  const selectedMarker = selectedNodeId ? markers[selectedNodeId] : undefined
  const selectedCameraId = selectedMarker?.cameraId ?? null
  const selectedPlacement = here.find((p) => p.cameraId === selectedCameraId)
  const figureById = useMemo(
    () => new Map(spaceData.figures.map((figure) => [figure.id, figure])),
    [spaceData.figures],
  )

  useEffect(() => {
    void dispatch(loadSpaces())
    void dispatch(loadPlacements())
    const cams = window.setInterval(() => void dispatch(loadCameras()), CAMERA_POLL_MS)
    const places = window.setInterval(() => void dispatch(loadPlacements()), PLACEMENT_POLL_MS)
    return () => {
      window.clearInterval(cams)
      window.clearInterval(places)
    }
  }, [dispatch])

  // The camera node behind the placement: the canonical node for the reference, its picture for
  // the other placements. Its state, not the camera's recording status, paints the marker.
  const nodeForPlacement = (p: { cameraId: string; isReference: boolean }) =>
    spaceData.markers.find((m) => m.cameraId === p.cameraId && (p.isReference ? !m.ownerId : !!m.ownerId))
      ?? spaceData.markers.find((m) => m.cameraId === p.cameraId)
  const placementColor = (p: { cameraId: string; isReference: boolean }) => {
    const node = nodeForPlacement(p)
    if (!node) return DEFAULT_NODE_COLOR
    return nodeStyle(figureById.get(node.id), statesById[node.id], alarmsById[node.id], descriptions).color
  }

  const cameraFigureIds = useMemo(
    () => new Set(spaceData.markers
      .filter((m) => m.cameraId && here.some((p) => p.cameraId === m.cameraId))
      .map((m) => m.id)),
    [spaceData.markers, here],
  )

  // The counters live on the space nodes; every watched space with an alarm below it is marked.
  const alarmed = useMemo(() => {
    const ids = new Set<string>()
    for (const candidate of spaces) {
      const id = candidate.id
      if (!id) continue
      const alarm = alarmsById[id]
      if (alarm?.alarm || (alarm?.childrenAlarms ?? 0) > 0) ids.add(id)
    }
    return ids
  }, [spaces, alarmsById])

  // The alarm list: running alarms from the states, alarms that just ended from the journal, so
  // the pale ring survives NORM.
  const alarmEntries = useMemo(() => {
    // Recent events are already pruned by the poll that fetched them; the render itself must stay
    // pure, so no clock is read here.
    const latestEvent = new Map<string, MapEvent>()
    for (const event of recentAlarms) {
      if (!event.objectId) continue
      const known = latestEvent.get(event.objectId)
      const end = alarmEndMs(event) ?? Number.POSITIVE_INFINITY
      const knownEnd = known ? alarmEndMs(known) ?? Number.POSITIVE_INFINITY : Number.NEGATIVE_INFINITY
      if (end > knownEnd) latestEvent.set(event.objectId, event)
    }

    const nameOf = (nodeId: string) => {
      const marker = markers[nodeId]
      const cameraName = marker?.cameraId ? camerasById[marker.cameraId]?.name : undefined
      return marker?.name || cameraName || nodeId
    }
    const causeOf = (event?: MapEvent) =>
      [event?.param1 || event?.param0, event?.timestamp ? clock(event.timestamp) : ''].filter(Boolean).join(' · ')

    const running: AlarmEntry[] = alarmStates
      .filter((alarm) => alarm.alarm && alarm.id)
      .map((alarm) => {
        const nodeId = alarm.id as string
        return {
          key: `state:${nodeId}`,
          nodeId,
          cameraId: markers[nodeId]?.cameraId,
          name: nameOf(nodeId),
          secondary: causeOf(latestEvent.get(nodeId)),
          active: true,
        }
      })

    const ended: AlarmEntry[] = [...latestEvent.entries()]
      .filter(([nodeId]) => !alarmStates.some((alarm) => alarm.id === nodeId && alarm.alarm))
      .flatMap(([nodeId, event]) => {
        if (alarmEndMs(event) === undefined) return []
        return [{
          key: `event:${event.id}`,
          nodeId,
          cameraId: markers[nodeId]?.cameraId,
          name: nameOf(nodeId),
          secondary: causeOf(event),
          active: false,
        }]
      })

    return [...running, ...ended]
  }, [alarmStates, recentAlarms, markers, camerasById])

  const byCamera = useMemo(() => {
    const result = new Map<string, 'active' | 'recent'>()
    for (const entry of alarmEntries) {
      if (entry.cameraId && (!result.has(entry.cameraId) || entry.active)) {
        result.set(entry.cameraId, entry.active ? 'active' : 'recent')
      }
    }
    return result
  }, [alarmEntries])

  // Aiming goes by where the camera really stands, which may be another space altogether.
  const reference = placements.find((p) => p.cameraId === selectedCameraId && p.isReference)
  const referenceSpace = spaces.find((s) => s.id === reference?.spaceId)
  const aimUnavailable = !selectedCameraId
    ? null
    : !reference
      ? t('maps.aimNoReference')
      : !sharesGeometry(referenceSpace, space ?? undefined) ? t('maps.aimOtherWorld', { name: referenceSpace?.name ?? '?' }) : null

  const resetAim = () => {
    setAiming(false)
    setAimTarget(null)
    setAimResult(null)
    setAimError(null)
  }

  const openSpace = (id: string) => {
    setSpaceId(id)
    writeLastSpace(id)
    setFocus(null)
  }

  const open = (id: string) => {
    openSpace(id)
    setSelectedNodeId(null)
    resetAim()
  }

  /** Opens the space a node belongs to and selects it; a camera is also brought into view. */
  const selectNode = (marker: MapMarker) => {
    resetAim()

    if (marker.nodeKind === 'space') openSpace(marker.id)

    const cameraId = marker.cameraId
    if (cameraId) {
      const placement = placements.find((p) => p.cameraId === cameraId && p.isReference)
        ?? placements.find((p) => p.cameraId === cameraId && p.spaceId === space?.id)
      if (placement && placement.spaceId !== space?.id) openSpace(placement.spaceId)
      if (placement) setFocus((f) => ({ lat: placement.lat, lon: placement.lon, seq: (f?.seq ?? 0) + 1 }))
    }

    setSelectedNodeId(marker.id)
  }

  /**
   * A camera picked on the canvas or in the alarm list: the node behind the placement may not be
   * among the loaded children yet, and its id is derived from the camera id, so ask the map to
   * resolve it.
   */
  const selectCamera = async (cameraId: string) => {
    const known = Object.values(markers).find((m) => m.cameraId === cameraId && !m.ownerId)
      ?? Object.values(markers).find((m) => m.cameraId === cameraId)
    if (known) {
      selectNode(known)
      return
    }

    const placement = placements.find((p) => p.cameraId === cameraId && p.isReference)
    const marker = await dispatch(resolveCameraNode({ cameraId, spaceId: placement?.spaceId })).unwrap()
    if (marker) selectNode(marker)
  }

  const aimAt = async (at: GeoPointDto) => {
    if (!selectedCameraId || !space?.id) return
    setAimTarget(at)
    setAimError(null)
    setAimResult(null)
    try {
      // A point picked on a plan is on its floor.
      setAimResult(await spatialRequest<PtzGeoAimResultDto>(`/api/ptz-geo/${encodeURIComponent(selectedCameraId)}/aim`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ spaceId: space.id, lat: at.lat, lon: at.lon, heightM: 0 }),
      }))
    } catch (e) {
      setAimError(errorText(e))
    }
  }

  if (loaded && !space) {
    return <Typography color="text.secondary" sx={{ p: 2 }}>{t('maps.noSpacesOperator')}</Typography>
  }
  if (!space) return null

  const alarmSection = {
    title: t('maps.alarms'),
    hint: t('maps.alarmsHint', { minutes: RECENT_ALARM_MINUTES }),
    empty: t('maps.noAlarms'),
    items: alarmEntries.map((entry) => ({
      key: entry.key,
      primary: entry.name,
      secondary: entry.secondary,
      dot: entry.active ? ALARM_COLOR : `${ALARM_COLOR}66`,
      selected: entry.nodeId === selectedNodeId,
      onClick: () => {
        const marker = markers[entry.nodeId]
        if (marker) selectNode(marker)
        else if (entry.cameraId) void selectCamera(entry.cameraId)
      },
    })),
  }

  return (
    <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', gap: 1 }}>
      <Box sx={{ flex: 1, minHeight: 0, display: 'flex', border: 1, borderColor: 'divider', borderRadius: 1, overflow: 'hidden' }}>
        <Box sx={{ width: 270, flexShrink: 0, borderRight: 1, borderColor: 'divider', display: 'flex', flexDirection: 'column', minHeight: 0 }}>
          <Tabs
            value={leftTab}
            onChange={(_, value: number) => setLeftTab(value)}
            variant="fullWidth"
            sx={{ minHeight: 36, flexShrink: 0, '& .MuiTab-root': { minHeight: 36, py: 0.5, minWidth: 0 } }}
          >
            <Tab icon={<AccountTreeIcon fontSize="small" />} iconPosition="start" label={t('maps.tree.tab')} />
            <Tab icon={<NotificationsActiveIcon fontSize="small" />} iconPosition="start" label={t('maps.alarms')} />
          </Tabs>
          <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
            {leftTab === 0 ? (
              <ObjectTree
                selectedId={selectedNodeId}
                onSelect={selectNode}
                checkedIds={checkedIds}
                onCheckedChange={setCheckedIds}
              />
            ) : (
              <MapObjectList sections={[alarmSection]} />
            )}
          </Box>
        </Box>

        <Box sx={{ flex: 1, minWidth: 0, display: 'flex' }}>
          <SpaceCanvas
            key={space.id}
            space={space}
            picking={aiming}
            focus={focus}
            onMapClick={(at) => (aiming ? void aimAt(at) : setSelectedNodeId(null))}
            overlay={(
              <>
                <SpaceNav spaces={spaces} space={space} onOpen={open} alarmed={alarmed} />
                <DetectionsToggle on={detections} onChange={(on) => { setDetections(on); writeDetections(on) }} />
              </>
            )}
          >
            {space.plan && <PlanOverlay space={space} />}
            {detections && space.id && <DetectionsLayer spaceId={space.id} />}
            <FiguresLayer
              figures={spaceData.figures}
              states={statesById}
              alarms={alarmsById}
              descriptions={descriptions}
              levels={levels}
              selectedId={selectedNodeId}
              onSelect={(id) => { const marker = markers[id]; if (marker) selectNode(marker) }}
              skipIds={cameraFigureIds}
            />
            <Footprints nested={nested} alarmed={alarmed} onPick={(s) => s.id && open(s.id)} />
            {here.map((p) => {
              const camera = camerasById[p.cameraId]
              return (
                <CameraMarker
                  key={p.cameraId}
                  placement={p}
                  label={camera?.name || p.cameraId}
                  color={placementColor(p)}
                  selected={p.cameraId === selectedCameraId}
                  dimmed={!!selectedCameraId && p.cameraId !== selectedCameraId}
                  alarm={byCamera.get(p.cameraId) ?? null}
                  onSelect={() => void selectCamera(p.cameraId)}
                />
              )
            })}
            {selectedPlacement && aimTarget && (
              <Polyline
                positions={[[selectedPlacement.lat, selectedPlacement.lon], [aimTarget.lat, aimTarget.lon]]}
                pathOptions={{ color: aimError ? '#ef5350' : '#ffca28', weight: 2, dashArray: '6 6' }}
                interactive={false}
              />
            )}
          </SpaceCanvas>
        </Box>

        {selectedNodeId && (
          <ObjectPanel
            key={selectedNodeId}
            nodeId={selectedNodeId}
            onClose={() => setSelectedNodeId(null)}
            onSelect={(id) => { const marker = markers[id]; if (marker) selectNode(marker) }}
            aiming={aiming}
            onAimingChange={setAiming}
            aimUnavailable={aimUnavailable}
            aimResult={aimResult}
            aimError={aimError}
          />
        )}
      </Box>
      <StateLegend
        ids={spaceData.markers.map((marker) => marker.id)}
        states={statesById}
        descriptions={descriptions}
      />
    </Box>
  )
}

/** What the colours on this canvas mean: the states its nodes carry, and the alarm colour. */
function StateLegend({
  ids, states, descriptions,
}: {
  ids: string[]
  states: Record<string, { states?: string[] }>
  descriptions: Record<string, { stateDescr?: string; stateColor?: string }>
}) {
  const { t } = useTranslation()
  const used = new Set<string>()
  for (const id of ids) {
    for (const state of states[id]?.states ?? []) used.add(state)
  }

  const entries = [...used]
    .map((name) => ({ name, description: descriptions[name] }))
    .filter((entry) => entry.description)

  return (
    <Box sx={{ display: 'flex', gap: 2, flexShrink: 0, flexWrap: 'wrap' }}>
      {entries.map(({ name, description }) => (
        <Box key={name} sx={{ display: 'flex', gap: 0.5, alignItems: 'center' }}>
          <Box sx={{ width: 10, height: 10, borderRadius: '50%', bgcolor: description?.stateColor || 'text.disabled' }} />
          <Typography variant="caption" color="text.secondary">{description?.stateDescr || name}</Typography>
        </Box>
      ))}
      <Box sx={{ display: 'flex', gap: 0.5, alignItems: 'center' }}>
        <Box sx={{ width: 10, height: 10, borderRadius: '50%', border: `2px solid ${ALARM_COLOR}` }} />
        <Typography variant="caption" color="text.secondary">{t('maps.alarm')}</Typography>
      </Box>
    </Box>
  )
}

/** Shows or hides detections on the map, in the map's corner above the scale. */
function DetectionsToggle({ on, onChange }: { on: boolean; onChange: (on: boolean) => void }) {
  const { t } = useTranslation()
  return (
    <Tooltip title={t('maps.detectionsHint')} placement="right">
      <ToggleButton
        value="detections"
        size="small"
        selected={on}
        onChange={() => onChange(!on)}
        sx={{ position: 'absolute', left: 10, bottom: 36, zIndex: 1000, py: 0.25, px: 1, bgcolor: 'rgba(40,40,40,0.85)', '&.Mui-selected': { bgcolor: 'rgba(40,40,40,0.95)' } }}
      >
        <PersonPinCircleIcon fontSize="small" sx={{ mr: 0.5 }} />
        {t('maps.detections')}
      </ToggleButton>
    </Tooltip>
  )
}

const clock = (iso: string) => new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
