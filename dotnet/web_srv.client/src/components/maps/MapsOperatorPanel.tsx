import { useEffect, useMemo, useState } from 'react'
import type { TFunction } from 'i18next'
import { Box, Tab, Tabs, ToggleButton, Tooltip, Typography } from '@mui/material'
import AccountTreeIcon from '@mui/icons-material/AccountTree'
import NotificationsActiveIcon from '@mui/icons-material/NotificationsActive'
import PersonPinCircleIcon from '@mui/icons-material/PersonPinCircle'
import { Polyline } from 'react-leaflet'
import { useTranslation } from 'react-i18next'
import type { AlarmDto, GeoPointDto, PtzGeoAimResultDto } from '../../dto/dtos'
import type { MapMarker } from '../../dto/mapDtos'
import { useAppDispatch, useAppSelector } from '../../store'
import { loadCameras } from '../../store/liveSlice'
import { errorText, loadPlacements, loadSpaces, spatialRequest } from '../../store/spatialSlice'
import { loadChildren, resolveCameraNode } from '../../store/objectsSlice'
import { eventSource } from '../EventDetailsPanel'
import SpaceCanvas, { type MapFocus } from '../../map/SpaceCanvas'
import PlanOverlay from '../../map/PlanOverlay'
import { CameraMarker } from '../../map/markers'
import { Footprints } from '../../map/footprints'
import DetectionsLayer from '../../map/DetectionsLayer'
import { ALARM_COLOR, STATUS_COLOR, cameraMapStatus, type CameraMapStatus } from '../../map/cameraStatus'
import { RECENT_ALARM_MINUTES, useAlarms } from '../../hooks/useAlarms'
import ObjectTree from '../objects/ObjectTree'
import ObjectPanel from '../objects/ObjectPanel'
import SpaceNav from './SpaceNav'
import MapObjectList from './MapObjectList'
import { alarmedSpaces, childrenOf as spacesChildrenOf, rootsOf, sharesGeometry } from './spatialUtils'

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

/**
 * Operator → Map: the one tree of nodes on the left, the space where the chosen node stands in the
 * middle, and the node itself on the right — a camera with its card, anything else read only.
 * The canvas still draws spaces and placements from the spatial module; the tree, the states and
 * the panel come from the map.
 */
export default function MapsOperatorPanel() {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const spaces = useAppSelector((s) => s.spatial.spaces)
  const placements = useAppSelector((s) => s.spatial.placements)
  const loaded = useAppSelector((s) => s.spatial.loaded)
  const camerasById = useAppSelector((s) => s.live.cameraStatesById)
  const markers = useAppSelector((s) => s.objects.markers)
  const childrenByParent = useAppSelector((s) => s.objects.childrenByParent)

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
  const { alarms, byCamera } = useAlarms()

  const space = spaces.find((s) => s.id === spaceId) ?? rootsOf(spaces)[0] ?? null
  const here = placements
    .filter((p) => p.spaceId === space?.id && camerasById[p.cameraId])
    .sort((a, b) => (camerasById[a.cameraId].name || a.cameraId).localeCompare(camerasById[b.cameraId].name || b.cameraId))
  const nested = space?.id ? spacesChildrenOf(spaces, space.id) : []

  const selectedMarker = selectedNodeId ? markers[selectedNodeId] : undefined
  const selectedCameraId = selectedMarker?.cameraId ?? null
  const selectedPlacement = here.find((p) => p.cameraId === selectedCameraId)
  // Only running alarms light the outlines: one that ended is history, not somewhere to go now.
  const alarmed = useMemo(() => alarmedSpaces(
    spaces, placements, new Set([...byCamera].filter(([, state]) => state === 'active').map(([id]) => id)),
  ), [spaces, placements, byCamera])

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

  // The children of the open space are what a camera marker is named by; load them even when the
  // operator has not expanded that level of the tree.
  useEffect(() => {
    if (space?.id && !childrenByParent[space.id]?.loaded) {
      void dispatch(loadChildren({ parentId: space.id }))
    }
  }, [space?.id, childrenByParent, dispatch])

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
   * A camera picked on the canvas: the node behind the placement may not be among the loaded
   * children yet, and its id is derived from the camera id, so ask the map to resolve it.
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

  /** Shows an alarm's camera: here if it is drawn in this space, otherwise where it really stands. */
  const goToAlarm = (alarm: AlarmDto) => {
    void selectCamera(alarm.cameraId)
  }

  if (loaded && !space) {
    return <Typography color="text.secondary" sx={{ p: 2 }}>{t('maps.noSpacesOperator')}</Typography>
  }
  if (!space) return null

  const alarmSection = {
    title: t('maps.alarms'),
    hint: t('maps.alarmsHint', { minutes: RECENT_ALARM_MINUTES }),
    empty: t('maps.noAlarms'),
    items: alarms.filter((a) => camerasById[a.cameraId]).map((a) => {
      const where = placements.find((p) => p.cameraId === a.cameraId && p.isReference)
      const whereName = spaces.find((s) => s.id === where?.spaceId)?.name
      return {
        key: a.eventUuid,
        primary: camerasById[a.cameraId].name || a.cameraId,
        secondary: [alarmCause(a, t), alarmTime(a, t), whereName].filter(Boolean).join(' · '),
        dot: a.active ? ALARM_COLOR : `${ALARM_COLOR}66`,
        selected: a.cameraId === selectedCameraId,
        onClick: () => goToAlarm(a),
      }
    }),
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
            <Footprints nested={nested} alarmed={alarmed} onPick={(s) => s.id && open(s.id)} />
            {here.map((p) => {
              const camera = camerasById[p.cameraId]
              return (
                <CameraMarker
                  key={p.cameraId}
                  placement={p}
                  label={camera?.name || p.cameraId}
                  color={STATUS_COLOR[cameraMapStatus(camera)]}
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
      <StatusLegend />
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

/** What set an alarm off, in a word: the last part of the ONVIF topic, or where it came from. */
function alarmCause(a: AlarmDto, t: TFunction): string {
  if (a.trigger) return a.trigger.split('/').pop() || a.trigger
  const source = eventSource(a)
  return source === 'analytics' ? t('events.sourceAnalytics') : source === 'onvif' ? t('events.sourceOnvif') : ''
}

const clock = (iso: string) => new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })

/** "since 12:03" while it runs, "12:03–12:05" once it ended. */
function alarmTime(a: AlarmDto, t: TFunction): string {
  return a.active || !a.alarmEndTs
    ? t('maps.alarmSince', { time: clock(a.alarmStartTs) })
    : `${clock(a.alarmStartTs)}–${clock(a.alarmEndTs)}`
}

function StatusLegend() {
  const { t } = useTranslation()
  const order: CameraMapStatus[] = ['recording', 'live', 'quiet', 'disabled']
  return (
    <Box sx={{ display: 'flex', gap: 2, flexShrink: 0 }}>
      {order.map((s) => (
        <Box key={s} sx={{ display: 'flex', gap: 0.5, alignItems: 'center' }}>
          <Box sx={{ width: 10, height: 10, borderRadius: '50%', bgcolor: STATUS_COLOR[s] }} />
          <Typography variant="caption" color="text.secondary">{t(`maps.status.${s}`)}</Typography>
        </Box>
      ))}
      <Box sx={{ display: 'flex', gap: 0.5, alignItems: 'center' }}>
        <Box sx={{ width: 10, height: 10, borderRadius: '50%', border: `2px solid ${ALARM_COLOR}` }} />
        <Typography variant="caption" color="text.secondary">{t('maps.alarm')}</Typography>
      </Box>
    </Box>
  )
}
