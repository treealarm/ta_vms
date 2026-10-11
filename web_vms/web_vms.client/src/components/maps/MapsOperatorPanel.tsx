import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  Alert, Box, Button, Chip, IconButton, Stack, ToggleButton, Tooltip, Typography,
} from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import GpsFixedIcon from '@mui/icons-material/GpsFixed'
import PersonPinCircleIcon from '@mui/icons-material/PersonPinCircle'
import { Polyline } from 'react-leaflet'
import { useTranslation } from 'react-i18next'
import type { CameraSetupDto, GeoPointDto, PtzGeoAimResultDto } from '../../dto/dtos'
import { useAppDispatch, useAppSelector } from '../../store'
import { loadCameras } from '../../store/liveSlice'
import { errorText, loadPlacements, loadSpaces, spatialRequest } from '../../store/spatialSlice'
import SpaceCanvas, { type MapFocus } from '../../map/SpaceCanvas'
import PlanOverlay from '../../map/PlanOverlay'
import { CameraMarker } from '../../map/markers'
import { Footprints } from '../../map/footprints'
import DetectionsLayer from '../../map/DetectionsLayer'
import { cameraMapStatus, STATUS_COLOR, type CameraMapStatus } from '../../map/cameraStatus'
import CameraStream from '../CameraStream'
import PtzControlPad from '../PtzControlPad'
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

/**
 * Operator → Map: the cameras of a space where they stand, coloured by what they are doing, and the
 * spaces nested in it as outlines to dive into. Click a camera to see its picture, drive it, or —
 * for a calibrated PTZ — point it at a place. Only cameras the operator may watch are listed by the
 * server, so only those are drawn.
 */
export default function MapsOperatorPanel() {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const spaces = useAppSelector((s) => s.spatial.spaces)
  const placements = useAppSelector((s) => s.spatial.placements)
  const loaded = useAppSelector((s) => s.spatial.loaded)
  const camerasById = useAppSelector((s) => s.live.cameraStatesById)

  const [spaceId, setSpaceId] = useState<string | null>(readLastSpace)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [aiming, setAiming] = useState(false)
  const [aimTarget, setAimTarget] = useState<GeoPointDto | null>(null)
  const [aimResult, setAimResult] = useState<PtzGeoAimResultDto | null>(null)
  const [aimError, setAimError] = useState<string | null>(null)
  const [focus, setFocus] = useState<MapFocus | null>(null)
  const [detections, setDetections] = useState(readDetections)

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

  const space = spaces.find((s) => s.id === spaceId) ?? rootsOf(spaces)[0] ?? null
  const here = placements
    .filter((p) => p.spaceId === space?.id && camerasById[p.cameraId])
    .sort((a, b) => (camerasById[a.cameraId].name || a.cameraId).localeCompare(camerasById[b.cameraId].name || b.cameraId))
  const nested = space?.id ? childrenOf(spaces, space.id) : []
  const selected = selectedId ? camerasById[selectedId] : undefined
  const selectedPlacement = here.find((p) => p.cameraId === selectedId)

  // Aiming goes by where the camera really stands, which may be another space altogether.
  const reference = placements.find((p) => p.cameraId === selectedId && p.isReference)
  const referenceSpace = spaces.find((s) => s.id === reference?.spaceId)
  const aimUnavailable = !reference
    ? t('maps.aimNoReference')
    : !sharesGeometry(referenceSpace, space ?? undefined) ? t('maps.aimOtherWorld', { name: referenceSpace?.name ?? '?' }) : null

  const select = (cameraId: string | null) => {
    setSelectedId(cameraId)
    setAiming(false)
    setAimTarget(null)
    setAimResult(null)
    setAimError(null)
  }

  const open = (id: string) => {
    setSpaceId(id)
    writeLastSpace(id)
    setFocus(null)
    select(null)
  }

  const aimAt = async (at: GeoPointDto) => {
    if (!selectedId || !space?.id) return
    setAimTarget(at)
    setAimError(null)
    setAimResult(null)
    try {
      // A point picked on a plan is on its floor.
      setAimResult(await spatialRequest<PtzGeoAimResultDto>(`/api/ptz-geo/${encodeURIComponent(selectedId)}/aim`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ spaceId: space.id, lat: at.lat, lon: at.lon, heightM: 0 }),
      }))
    } catch (e) {
      setAimError(errorText(e))
    }
  }

  /** Selects a camera in this space and brings it into view. */
  const goTo = (cameraId: string) => {
    const p = here.find((x) => x.cameraId === cameraId)
    if (!p) return
    select(cameraId)
    setFocus((f) => ({ lat: p.lat, lon: p.lon, seq: (f?.seq ?? 0) + 1 }))
  }

  if (loaded && !space) {
    return <Typography color="text.secondary" sx={{ p: 2 }}>{t('maps.noSpacesOperator')}</Typography>
  }
  if (!space) return null

  return (
    <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', gap: 1 }}>
      <Box sx={{ flex: 1, minHeight: 0, display: 'flex', border: 1, borderColor: 'divider', borderRadius: 1, overflow: 'hidden' }}>
        <Box sx={{ width: 240, flexShrink: 0, borderRight: 1, borderColor: 'divider', overflowY: 'auto' }}>
          <MapObjectList
            sections={[
              {
                title: t('maps.cameras'),
                empty: t('maps.noCamerasHereOperator'),
                items: here.map((p) => {
                  const camera = camerasById[p.cameraId]
                  return {
                    key: p.cameraId,
                    primary: camera.name || p.cameraId,
                    dot: STATUS_COLOR[cameraMapStatus(camera)],
                    selected: p.cameraId === selectedId,
                    onClick: () => goTo(p.cameraId),
                  }
                }),
              },
              ...(nested.length ? [{
                title: t('maps.inside'),
                items: nested.map((s) => ({ key: s.id ?? s.name, primary: s.name, onClick: () => s.id && open(s.id) })),
              }] : []),
            ]}
          />
        </Box>
        <Box sx={{ flex: 1, minWidth: 0, display: 'flex' }}>
          <SpaceCanvas
            key={space.id}
            space={space}
            picking={aiming}
            focus={focus}
            onMapClick={(at) => (aiming ? void aimAt(at) : select(null))}
            overlay={(
              <>
                <SpaceNav spaces={spaces} space={space} onOpen={open} />
                <DetectionsToggle on={detections} onChange={(on) => { setDetections(on); writeDetections(on) }} />
              </>
            )}
          >
            {space.plan && <PlanOverlay space={space} />}
            {detections && space.id && <DetectionsLayer spaceId={space.id} />}
            <Footprints nested={nested} onPick={(s) => s.id && open(s.id)} />
            {here.map((p) => {
              const camera = camerasById[p.cameraId]
              return (
                <CameraMarker
                  key={p.cameraId}
                  placement={p}
                  label={camera?.name || p.cameraId}
                  color={STATUS_COLOR[cameraMapStatus(camera)]}
                  selected={p.cameraId === selectedId}
                  dimmed={!!selectedId && p.cameraId !== selectedId}
                  onSelect={() => select(p.cameraId)}
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
        {selected && (
          <CameraSidePanel
            camera={selected}
            aiming={aiming}
            onAimingChange={setAiming}
            aimUnavailable={aimUnavailable}
            aimResult={aimResult}
            aimError={aimError}
            onClose={() => select(null)}
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

type SideProps = {
  camera: CameraSetupDto
  aiming: boolean
  onAimingChange: (on: boolean) => void
  /** Why this camera cannot be aimed at a point here, or null when it can. */
  aimUnavailable: string | null
  aimResult: PtzGeoAimResultDto | null
  aimError: string | null
  onClose: () => void
}

function CameraSidePanel({ camera, aiming, onAimingChange, aimUnavailable, aimResult, aimError, onClose }: SideProps) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const monitors = useAppSelector((s) => s.monitors.monitors)
  const status = cameraMapStatus(camera)
  const stream = (camera.streams ?? []).find((s) => s.webRtc && s.whepUrl && !s.disabled)
  const monitor = monitors.find((m) => m.cameraIds.includes(camera.cameraId) || m.slots.some((s) => s.cameraId === camera.cameraId))

  return (
    <Stack spacing={1} sx={{ width: 360, flexShrink: 0, borderLeft: 1, borderColor: 'divider', p: 1.5, overflowY: 'auto' }}>
      <Stack direction="row" alignItems="center" spacing={1}>
        <Typography variant="subtitle1" fontWeight={600} noWrap sx={{ flex: 1 }}>{camera.name || camera.cameraId}</Typography>
        <Chip size="small" label={t(`maps.status.${status}`)} sx={{ bgcolor: STATUS_COLOR[status], color: '#000' }} />
        <IconButton size="small" onClick={onClose}><CloseIcon fontSize="small" /></IconButton>
      </Stack>
      <Box sx={{ position: 'relative', width: '100%', aspectRatio: '16 / 9', bgcolor: '#000', borderRadius: 1, overflow: 'hidden' }}>
        {stream?.whepUrl && !camera.disabled
          ? <CameraStream key={camera.cameraId} whepUrl={stream.whepUrl} stunUrl={camera.effectiveWebrtcStunUrl} cameraId={camera.cameraId} />
          : <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>{t('maps.noLive')}</Typography>}
      </Box>
      {monitor && (
        <Button size="small" variant="outlined" onClick={() => navigate(`/operator/monitors?monitorId=${encodeURIComponent(monitor.monitorId)}`)}>
          {t('maps.openInMonitor', { name: monitor.name })}
        </Button>
      )}
      {camera.hasTelemetry && (
        <>
          <PtzControlPad cameraId={camera.cameraId} profileToken={camera.streams?.[0]?.profileToken ?? ''} />
          <Tooltip title={aimUnavailable ?? t('maps.aimHint')}>
            <span>
              <ToggleButton size="small" value="aim" fullWidth disabled={!!aimUnavailable} selected={aiming} onChange={() => onAimingChange(!aiming)}>
                <GpsFixedIcon fontSize="small" sx={{ mr: 1 }} />{t('maps.aimHere')}
              </ToggleButton>
            </span>
          </Tooltip>
          {aimUnavailable && <Typography variant="caption" color="text.secondary">{aimUnavailable}</Typography>}
          {aimError && <Alert severity="error">{aimError}</Alert>}
          {aimResult && (
            <Alert severity={aimResult.extrapolated ? 'warning' : 'success'}>
              {t('maps.aimed', { dist: aimResult.distanceM.toFixed(0), az: aimResult.azimuthDeg.toFixed(0) })}
              {aimResult.extrapolated && ` ${t('ptzGeo.extrapolated')}`}
            </Alert>
          )}
        </>
      )}
    </Stack>
  )
}

function StatusLegend() {
  const { t } = useTranslation()
  const order: CameraMapStatus[] = ['recording', 'live', 'quiet', 'disabled']
  return (
    <Stack direction="row" spacing={2} sx={{ flexShrink: 0 }}>
      {order.map((s) => (
        <Stack key={s} direction="row" spacing={0.5} alignItems="center">
          <Box sx={{ width: 10, height: 10, borderRadius: '50%', bgcolor: STATUS_COLOR[s] }} />
          <Typography variant="caption" color="text.secondary">{t(`maps.status.${s}`)}</Typography>
        </Stack>
      ))}
    </Stack>
  )
}
