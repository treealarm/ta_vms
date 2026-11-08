import { useEffect, useMemo, useState } from 'react'
import {
  Alert, Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel,
  Radio, RadioGroup, Tab, Tabs, TextField, ToggleButton, Tooltip, Typography,
} from '@mui/material'
import { useSearchParams } from 'react-router-dom'
import AccountTreeIcon from '@mui/icons-material/AccountTree'
import CheckCircleOutlineIcon from '@mui/icons-material/CheckCircleOutline'
import NotificationsActiveIcon from '@mui/icons-material/NotificationsActive'
import PersonPinCircleIcon from '@mui/icons-material/PersonPinCircle'
import SearchIcon from '@mui/icons-material/Search'
import TableRowsIcon from '@mui/icons-material/TableRows'
import { Polyline } from 'react-leaflet'
import { useTranslation } from 'react-i18next'
import type { GeoPointDto, PtzGeoAimResultDto } from '../../dto/dtos'
import type { MapAlarm, MapMarker } from '../../dto/mapDtos'
import { useAppDispatch, useAppSelector } from '../../store'
import { acknowledgeAlarm, closeAlarm, loadAlarms } from '../../store/alarmsSlice'
import { loadCameras } from '../../store/liveSlice'
import { errorText, loadPlacements, loadSpaces, spatialRequest } from '../../store/spatialSlice'
import { loadNodes, resolveCameraNode, revealNode } from '../../store/objectsSlice'
import { clearTracks } from '../../store/tracksSlice'
import { RECENT_ALARM_MINUTES } from '../../store/eventsSlice'
import type { CameraPlacementDto } from '../../dto/dtos'
import { useMapSpace } from '../../hooks/useMapSpace'
import { useAlarmFeed } from '../../hooks/useAlarmFeed'
import SpaceCanvas, { type MapFocus } from '../../map/SpaceCanvas'
import PlanOverlay from '../../map/PlanOverlay'
import FiguresLayer from '../../map/FiguresLayer'
import TracksLayer from '../../map/TracksLayer'
import { figureAtPlacement, pointsCentre } from '../../map/geo'
import { enterZoom } from '../../map/zoomLevels'
import { CameraMarker } from '../../map/markers'
import { Footprints } from '../../map/footprints'
import DetectionsLayer from '../../map/DetectionsLayer'
import { ALARM_COLOR, ALARM_STATE, DEFAULT_NODE_COLOR, nodeStyle } from '../../map/nodeStyle'
import ObjectTree from '../objects/ObjectTree'
import ObjectPanel from '../objects/ObjectPanel'
import ObjectSearch from '../objects/ObjectSearch'
import DiagramViewer from '../diagrams/DiagramViewer'
import StatesTable from '../events/StatesTable'
import SpaceNav from './SpaceNav'
import MapObjectList, { type MapObjectItem } from './MapObjectList'
import { rootsOf, sharesGeometry } from './spatialUtils'

/** The camera list carries recording state and stream freshness; this is how stale the map may get. */
const CAMERA_POLL_MS = 5000
/** The alarm entity changes when an operator acts or a condition moves; a short poll is enough. */
const ALARM_POLL_MS = 5000
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

/** What the alarm dialog is open for, and what it will do when confirmed. */
type AlarmDialogState = { alarm: MapAlarm; action: 'acknowledge' | 'close' }

/**
 * Operator → Map: the one tree of nodes on the left, the space where the chosen node stands in the
 * middle — its arbitrary figures painted by their states — and the node itself on the right.
 * The alarm list is the map's alarm entity, not the ALARM state: the state says something is wrong
 * and paints the map, the alarm says who has to deal with it.
 */
export default function MapsOperatorPanel() {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const [searchParams] = useSearchParams()
  // The node the journal asked to open: `?node=<uuid>` when an alarm event was clicked.
  const requestedNode = searchParams.get('node')
  const spaces = useAppSelector((s) => s.spatial.spaces)
  const placements = useAppSelector((s) => s.spatial.placements)
  const loaded = useAppSelector((s) => s.spatial.loaded)
  const camerasById = useAppSelector((s) => s.live.cameraStatesById)
  const markers = useAppSelector((s) => s.objects.markers)
  const statesById = useAppSelector((s) => s.states.states)
  const alarmsById = useAppSelector((s) => s.states.alarms)
  const descriptions = useAppSelector((s) => s.states.descriptions)
  const openAlarms = useAppSelector((s) => s.alarms.open)
  const closedAlarms = useAppSelector((s) => s.alarms.closed)
  const alarmsMore = useAppSelector((s) => s.alarms.openHasMore || s.alarms.closedHasMore)
  const levels = useAppSelector((s) => s.levels.levels)
  const tracks = useAppSelector((s) => s.tracks.tracks)
  const tracksSpaceId = useAppSelector((s) => s.tracks.spaceId)

  const [spaceId, setSpaceId] = useState<string | null>(readLastSpace)
  // A mnemoscheme takes the middle of the page in place of the space canvas.
  const [diagramId, setDiagramId] = useState<string | null>(null)
  const [selectedNodeId, setSelectedNodeId] = useState<string | null>(null)
  const [checkedIds, setCheckedIds] = useState<Set<string>>(new Set())
  // The open rows of the tree; a search result unfolds the path down to itself.
  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const [leftTab, setLeftTab] = useState(0)
  const [aiming, setAiming] = useState(false)
  const [aimTarget, setAimTarget] = useState<GeoPointDto | null>(null)
  const [aimResult, setAimResult] = useState<PtzGeoAimResultDto | null>(null)
  const [aimError, setAimError] = useState<string | null>(null)
  const [focus, setFocus] = useState<MapFocus | null>(null)
  const [detections, setDetections] = useState(readDetections)
  // The row an operator picked and the action they picked for it; confirmed in one dialog.
  const [alarmDialog, setAlarmDialog] = useState<AlarmDialogState | null>(null)
  const [alarmComment, setAlarmComment] = useState('')
  const [alarmReason, setAlarmReason] = useState<'false_alarm' | 'other'>('false_alarm')
  const [alarmError, setAlarmError] = useState<string | null>(null)
  const [alarmBusy, setAlarmBusy] = useState(false)

  const space = spaces.find((s) => s.id === spaceId) ?? rootsOf(spaces)[0] ?? null
  const spaceData = useMapSpace(space)
  useAlarmFeed()

  // The entities themselves: open rows and the ones closed in the last minutes, from VmsMap.
  useEffect(() => {
    void dispatch(loadAlarms({}))
    const timer = window.setInterval(() => void dispatch(loadAlarms({})), ALARM_POLL_MS)
    return () => window.clearInterval(timer)
  }, [dispatch])

  const here = placements
    .filter((p) => p.spaceId === space?.id && camerasById[p.cameraId])
    .sort((a, b) => (camerasById[a.cameraId].name || a.cameraId).localeCompare(camerasById[b.cameraId].name || b.cameraId))
  // The spaces whose footprint is drawn on this canvas, wherever the tree holds them: the canvas
  // decides what is drawn, not the parent link.
  const nested = useMemo(() => {
    const onCanvas = new Set(spaceData.markers.filter((m) => m.nodeKind === 'space').map((m) => m.id))
    return spaces.filter((s) => !!s.id && onCanvas.has(s.id))
  }, [spaceData.markers, spaces])

  const selectedMarker = selectedNodeId ? markers[selectedNodeId] : undefined
  const selectedCameraId = selectedMarker?.cameraId ?? null
  const selectedPlacement = here.find((p) => p.cameraId === selectedCameraId)
  const figureById = useMemo(
    () => new Map(spaceData.figures.map((figure) => [figure.id, figure])),
    [spaceData.figures],
  )
  // The layer each nested space's footprint belongs to, from its own polygon on this canvas.
  const spaceZoomLevels = useMemo(() => {
    const bySpace: Record<string, string | undefined> = {}
    for (const s of spaces) {
      if (s.id) bySpace[s.id] = figureById.get(s.id)?.zoomLevel
    }
    return bySpace
  }, [spaces, figureById])

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

  // The node behind a placement is always a replica of the camera: the canonical node stands under
  // its server and is never drawn on a canvas. A reference and a picture differ by the flag, not by
  // the node, so the matching marker is the one with an owner.
  const nodeForPlacement = (p: { cameraId: string }) =>
    figureAtPlacement(spaceData.figures, p as CameraPlacementDto)
      ?? spaceData.markers.find((m) => m.cameraId === p.cameraId)
  const placementColor = (p: { cameraId: string; isReference: boolean }) => {
    const node = nodeForPlacement(p)
    if (!node) return DEFAULT_NODE_COLOR
    return nodeStyle(figureById.get(node.id), statesById[node.id], alarmsById[node.id], descriptions).color
  }

  // Only the picture a placement draws: a camera may have another replica in the same space — a
  // diagram of its readings — and that one is an ordinary figure, not the marker's stand-in.
  const cameraFigureIds = useMemo(() => {
    const ids = new Set<string>()
    for (const placement of here) {
      const figure = figureAtPlacement(spaceData.figures, placement)
      if (figure) ids.add(figure.id)
    }
    return ids
  }, [spaceData.figures, here])

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

  // The alarm entity, newest first. A row whose condition is still there is a running alarm; a
  // closed one stays visible for the window VmsMap serves, so the ring survives NORM.
  const alarmRows = useMemo(
    () => [...openAlarms, ...closedAlarms],
    [openAlarms, closedAlarms],
  )

  const byCamera = useMemo(() => {
    const result = new Map<string, 'active' | 'recent'>()
    for (const alarm of alarmRows) {
      const cameraId = alarm.cameraId || (alarm.objectId ? markers[alarm.objectId]?.cameraId : undefined)
      if (!cameraId) continue
      const active = alarm.status !== 'closed'
      if (!result.has(cameraId) || active) result.set(cameraId, active ? 'active' : 'recent')
    }
    return result
  }, [alarmRows, markers])

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
    // Tracks were read for one space's rectangle; another space asks a different question.
    if (id !== space?.id) void dispatch(clearTracks())

    setDiagramId(null)
    setSpaceId(id)
    writeLastSpace(id)
    setFocus(null)
  }

  const open = (id: string) => {
    openSpace(id)
    setSelectedNodeId(null)
    resetAim()
  }

  /** Opens the canvas a node belongs to and selects it; a camera is also brought into view. */
  const selectNode = (marker: MapMarker) => {
    resetAim()

    if (marker.nodeKind === 'space') openSpace(marker.id)
    if (marker.nodeKind === 'diagram') setDiagramId(marker.id)

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
    const known = Object.values(markers).find((m) => m.cameraId === cameraId && !!m.ownerId)
      ?? Object.values(markers).find((m) => m.cameraId === cameraId)
    if (known) {
      selectNode(known)
      return
    }

    const placement = placements.find((p) => p.cameraId === cameraId && p.isReference)
    const marker = await dispatch(resolveCameraNode({ cameraId, spaceId: placement?.spaceId })).unwrap()
    if (marker) selectNode(marker)
  }

  /**
   * A node picked in the search tab: unfold the tree down to it, open the space it stands in and
   * select it. A camera then goes by its reference placement, like one picked anywhere else.
   */
  const revealFromSearch = async (marker: MapMarker) => {
    resetAim()

    // The chain from the root down to its parent, with every page along it read.
    const ancestors = await dispatch(revealNode(marker)).unwrap().catch(() => [] as MapMarker[])

    // The canvas it stands on: itself for a space or a diagram, else the nearest one above it.
    const canvas = marker.nodeKind === 'space' || marker.nodeKind === 'diagram'
      ? marker
      : [...ancestors].reverse()
        .find((ancestor) => ancestor.nodeKind === 'space' || ancestor.nodeKind === 'diagram')

    if (canvas?.nodeKind === 'diagram') setDiagramId(canvas.id)
    else if (canvas?.nodeKind === 'space') openSpace(canvas.id)
    else if (marker.cameraId) {
      // Nothing in the tree places it on a canvas; its reference placement does.
      const placement = placements.find((p) => p.cameraId === marker.cameraId && p.isReference)
      if (placement) openSpace(placement.spaceId)
    }

    if (ancestors.length > 0) {
      setExpanded((previous) => {
        const next = new Set(previous)
        for (const ancestor of ancestors) next.add(ancestor.id)
        return next
      })
    }

    setSelectedNodeId(marker.id)

    // A node that is not a camera is drawn from its own shape, which this canvas may not know yet.
    if (!marker.cameraId) {
      const loaded = await dispatch(loadNodes([marker.id])).unwrap().catch(() => null)
      const figure = loaded?.figures.find((candidate) => candidate.id === marker.id)
      const centre = pointsCentre(figure?.geometry?.coord ?? [])
      if (centre) setFocus((previous) => ({ ...centre, seq: (previous?.seq ?? 0) + 1 }))
    }
  }

  /**
   * Enter a node that holds children. A space or a mnemoscheme opens its own canvas, which is a
   * world of its own; anything else on this canvas raises the zoom until its children are drawn
   * and centres on it -- never on its polygon, which may be a circle, an icon or nothing at all.
   */
  const enterNode = (nodeId: string) => {
    const marker = markers[nodeId]
    if (!marker) return
    resetAim()

    if (marker.nodeKind === 'space') {
      openSpace(marker.id)
      setSelectedNodeId(marker.id)
      return
    }
    if (marker.nodeKind === 'diagram') {
      setDiagramId(marker.id)
      setSelectedNodeId(marker.id)
      return
    }

    // The children drawn on this canvas carry their own scale layers.
    const children = spaceData.figures.filter((figure) => figure.parentId === nodeId)
    const zoom = enterZoom(children, levels)

    const centre = pointsCentre(figureById.get(nodeId)?.geometry?.coord ?? [])
      ?? pointsCentre(children.flatMap((child) => child.geometry?.coord ?? []))
    if (centre) {
      setFocus((previous) => ({
        ...centre,
        seq: (previous?.seq ?? 0) + 1,
        zoom,
        zoomStep: zoom === undefined,
      }))
    }
    setSelectedNodeId(nodeId)
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

  // Opened from the journal's alarm event: select the node the alarm stands on and show the list
  // it came from. The node may not be on this canvas, so it is loaded and revealed like a search hit.
  useEffect(() => {
    if (!requestedNode) return
    setLeftTab(1)
    setSelectedNodeId(requestedNode)
    void (async () => {
      const loaded = await dispatch(loadNodes([requestedNode])).unwrap().catch(() => null)
      const marker = loaded?.markers.find((candidate) => candidate.id === requestedNode)
      if (marker) await revealFromSearch(marker)
    })()
    // The reveal is the same one a search does; re-running it on every marker change would fight
    // the operator's own navigation.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [requestedNode, dispatch])

  if (loaded && !space) {
    return <Typography color="text.secondary" sx={{ p: 2 }}>{t('maps.noSpacesOperator')}</Typography>
  }
  if (!space) return null

  const openAlarmDialog = (alarm: MapAlarm, action: 'acknowledge' | 'close') => {
    setAlarmDialog({ alarm, action })
    setAlarmComment('')
    setAlarmReason('false_alarm')
    setAlarmError(null)
  }

  const submitAlarmDialog = async () => {
    if (!alarmDialog?.alarm.id) return

    setAlarmBusy(true)
    setAlarmError(null)
    try {
      if (alarmDialog.action === 'close') {
        await dispatch(closeAlarm({
          id: alarmDialog.alarm.id, reason: alarmReason, comment: alarmComment,
        })).unwrap()
      } else {
        await dispatch(acknowledgeAlarm({
          id: alarmDialog.alarm.id, comment: alarmComment,
        })).unwrap()
      }
      setAlarmDialog(null)
    } catch (e) {
      // 409 is the rule refusing a live condition on an armed node; show its own sentence.
      setAlarmError(errorText(e))
    } finally {
      setAlarmBusy(false)
    }
  }

  /** An alarm whose node is not on any loaded canvas yet: read it, then reveal it in the tree. */
  const openAlarmNode = async (objectId: string) => {
    const loaded = await dispatch(loadNodes([objectId])).unwrap().catch(() => null)
    const marker = loaded?.markers.find((candidate) => candidate.id === objectId)
    if (marker) await revealFromSearch(marker)
  }

  const alarmSecondary = (alarm: MapAlarm) => [
    t(`alarms.source.${alarm.source || 'sensor'}`),
    t(`alarms.status.${alarm.status || 'active'}`),
    t(`alarms.severity.${alarm.severity || 'warning'}`),
    alarm.raisedAt ? clock(alarm.raisedAt) : '',
  ].filter(Boolean).join(' · ')

  const alarmItems: MapObjectItem[] = alarmRows.map((alarm) => {
      const nodeId = alarm.objectId ?? ''
      const marker = markers[nodeId]
      const cameraId = alarm.cameraId || marker?.cameraId
      const active = alarm.status !== 'closed'
      return {
        key: alarm.id ?? `${nodeId}:${alarm.raisedAt}`,
        primary: alarm.objectName || marker?.name || (cameraId ? camerasById[cameraId]?.name : '') || nodeId,
        secondary: alarmSecondary(alarm),
        dot: active ? ALARM_COLOR : `${ALARM_COLOR}66`,
        selected: nodeId === selectedNodeId,
        onClick: () => {
          const clicked = markers[nodeId]
          if (clicked) selectNode(clicked)
          else if (cameraId) void selectCamera(cameraId)
          else void openAlarmNode(nodeId)
        },
        action: {
          title: alarm.canAck ? t('alarms.acknowledge') : t('alarms.cannotAcknowledge'),
          icon: <CheckCircleOutlineIcon fontSize="small" />,
          disabled: !alarm.canAck,
          onClick: () => openAlarmDialog(alarm, 'acknowledge'),
        },
      }
    })

  if (alarmsMore) {
    // The list is a keyset page, not the whole history; older rows arrive on request.
    alarmItems.push({
      key: 'alarms:more',
      primary: t('maps.tree.more'),
      onClick: () => void dispatch(loadAlarms({ more: true })),
    })
  }

  const alarmSection = {
    title: t('maps.alarms'),
    hint: t('maps.alarmsHint', { minutes: RECENT_ALARM_MINUTES }),
    empty: t('maps.noAlarms'),
    items: alarmItems,
  }

  return (
    <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', gap: 1 }}>
      <Box sx={{ flex: 1, minHeight: 0, display: 'flex', border: 1, borderColor: 'divider', borderRadius: 1, overflow: 'hidden' }}>
        <Box sx={{ width: 270, flexShrink: 0, borderRight: 1, borderColor: 'divider', display: 'flex', flexDirection: 'column', minHeight: 0 }}>
          <Tabs
            value={leftTab}
            onChange={(_, value: number) => setLeftTab(value)}
            variant="fullWidth"
            sx={{
              minHeight: 48,
              flexShrink: 0,
              '& .MuiTab-root': { minHeight: 48, py: 0.5, px: 0.25, minWidth: 0, fontSize: 11 },
            }}
          >
            <Tab icon={<AccountTreeIcon fontSize="small" />} label={t('maps.tree.tab')} />
            <Tab icon={<NotificationsActiveIcon fontSize="small" />} label={t('maps.alarms')} />
            <Tab icon={<TableRowsIcon fontSize="small" />} label={t('maps.states.tab')} />
            <Tab icon={<SearchIcon fontSize="small" />} label={t('maps.search.tab')} />
          </Tabs>
          <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
            {leftTab === 0 ? (
              <ObjectTree
                selectedId={selectedNodeId}
                onSelect={selectNode}
                checkedIds={checkedIds}
                onCheckedChange={setCheckedIds}
                expanded={expanded}
                onExpandedChange={setExpanded}
              />
            ) : leftTab === 1 ? (
              <MapObjectList sections={[alarmSection]} />
            ) : leftTab === 2 ? (
              <StatesTable ids={checkedIds} selectedId={selectedNodeId} onSelect={selectNode} />
            ) : (
              <ObjectSearch space={space} onSelect={(marker) => void revealFromSearch(marker)} />
            )}
          </Box>
        </Box>

        <Box sx={{ flex: 1, minWidth: 0, display: 'flex' }}>
          {diagramId ? (
            <DiagramViewer
              diagramId={diagramId}
              selectedId={selectedNodeId}
              onPick={(marker) => setSelectedNodeId(marker.id)}
              onOpen={(marker) => selectNode(marker)}
            />
          ) : (
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
              {tracksSpaceId === space.id && tracks.length > 0 && (
                <TracksLayer tracks={tracks} markers={markers} />
              )}
              <Footprints nested={nested} alarmed={alarmed} levels={levels} zoomLevels={spaceZoomLevels} onPick={(s) => s.id && open(s.id)} />
              {here.map((p) => {
                const camera = camerasById[p.cameraId]
                const node = nodeForPlacement(p)
                return (
                  <CameraMarker
                    key={p.cameraId}
                    placement={p}
                    label={camera?.name || p.cameraId}
                    color={placementColor(p)}
                    selected={p.cameraId === selectedCameraId}
                    dimmed={!!selectedCameraId && p.cameraId !== selectedCameraId}
                    alarm={byCamera.get(p.cameraId) ?? null}
                    levels={levels}
                    zoomLevel={node ? figureById.get(node.id)?.zoomLevel : undefined}
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
          )}
        </Box>

        {selectedNodeId && (
          <ObjectPanel
            key={selectedNodeId}
            nodeId={selectedNodeId}
            onClose={() => setSelectedNodeId(null)}
            onSelect={(id) => { const marker = markers[id]; if (marker) selectNode(marker) }}
            onEnter={enterNode}
            aiming={aiming}
            onAimingChange={setAiming}
            aimUnavailable={aimUnavailable}
            aimResult={aimResult}
            aimError={aimError}
          />
        )}
      </Box>
      {!diagramId && (
        <StateLegend
          ids={spaceData.markers.map((marker) => marker.id)}
          states={statesById}
          descriptions={descriptions}
        />
      )}

      {alarmDialog && (
        <Dialog open onClose={() => setAlarmDialog(null)} maxWidth="xs" fullWidth>
          <DialogTitle>{t('alarms.handle')}</DialogTitle>
          <DialogContent>
            <Typography variant="body2" sx={{ mb: 2 }}>
              {alarmDialog.alarm.objectName || alarmDialog.alarm.objectId} · {t(`alarms.status.${alarmDialog.alarm.status || 'active'}`)}
            </Typography>

            {alarmDialog.action === 'close' && (
              <RadioGroup
                row
                value={alarmReason}
                onChange={(_, value) => setAlarmReason(value as 'false_alarm' | 'other')}
              >
                <FormControlLabel value="false_alarm" control={<Radio size="small" />} label={t('alarms.reason.falseAlarm')} />
                <FormControlLabel value="other" control={<Radio size="small" />} label={t('alarms.reason.other')} />
              </RadioGroup>
            )}

            <TextField
              multiline
              minRows={2}
              fullWidth
              label={t('alarms.comment')}
              value={alarmComment}
              onChange={(event) => setAlarmComment(event.target.value)}
              sx={{ mt: 2 }}
            />

            {alarmError && <Alert severity="error" sx={{ mt: 2 }}>{alarmError}</Alert>}
          </DialogContent>
          <DialogActions>
            <Button onClick={() => setAlarmDialog(null)}>{t('common.cancel')}</Button>
            {alarmDialog.action === 'acknowledge' && alarmDialog.alarm.status === 'returned' && (
              <Button onClick={() => setAlarmDialog({ ...alarmDialog, action: 'close' })}>
                {t('alarms.close')}
              </Button>
            )}
            <Button
              variant="contained"
              disabled={alarmBusy || (alarmDialog.action === 'acknowledge' && !alarmDialog.alarm.canAck)}
              onClick={() => void submitAlarmDialog()}
            >
              {alarmDialog.action === 'close' ? t('alarms.close') : t('alarms.acknowledge')}
            </Button>
          </DialogActions>
        </Dialog>
      )}
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
    // The alarm dot below already stands for the ALARM state.
    .filter((name) => name !== ALARM_STATE)
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
