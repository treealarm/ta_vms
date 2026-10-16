import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  Alert, Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, IconButton, Stack, Table,
  TableBody, TableCell, TableHead, TableRow, Tooltip, Typography,
} from '@mui/material'
import MyLocationIcon from '@mui/icons-material/MyLocation'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import ZoomInIcon from '@mui/icons-material/ZoomIn'
import ClearIcon from '@mui/icons-material/Clear'
import { useTranslation } from 'react-i18next'
import type {
  LandmarkDto, PtzGeoAimResultDto, PtzGeoCalibrationDto, PtzGeoObservationDto, PtzStatusDto, SpaceDto,
} from '../../dto/dtos'
import { useAppSelector } from '../../store'
import { errorText, spatialRequest } from '../../store/spatialSlice'
import CameraStream from '../CameraStream'
import PtzControlPad from '../PtzControlPad'
import AimCrosshair from '../AimCrosshair'
import SpaceCanvas from '../../map/SpaceCanvas'
import PlanOverlay from '../../map/PlanOverlay'
import { CameraMarker, LandmarkMarker } from '../../map/markers'
import { STATUS_COLOR } from '../../map/cameraStatus'
import L from 'leaflet'

type Props = {
  cameraId: string
  /** Where the camera really stands: its reference placement is in this space. */
  space: SpaceDto
  /** The landmarks of that space. */
  landmarks: LandmarkDto[]
  onClose: () => void
}

// Same hold the slave survey keeps: long enough to outlast a slow request, renewed well before it
// runs out, and released by itself if the browser goes away.
const HOLD_SECONDS = 40
const HOLD_RENEW_MS = 15000

const fmt = (v: number | null | undefined, digits = 3) => (v === null || v === undefined ? '—' : v.toFixed(digits))

/**
 * Teaches a PTZ camera where its space is: centre it on a landmark, record where it points, repeat.
 * Two landmarks in different directions make a calibration; a third lets each one be checked
 * against the others. Picking a landmark turns the camera to it — by the calibration alone once
 * there is one, which is also the test that the camera really obeys absolute positions; before
 * that, back to where the landmark was recorded. The camera is held for the dialog alone while it
 * is open: nothing that aims cameras automatically may move it.
 *
 * A landmark also carries how far a slave zooms at its distance, recorded apart from its pan and
 * tilt: those are best taken zoomed right in, the framing zoom is how a person there should look.
 */
export default function PtzGeoCalibrationDialog({ cameraId, space, landmarks, onClose }: Props) {
  const { t } = useTranslation()
  const camera = useAppSelector((s) => s.live.cameraStatesById[cameraId])
  const placement = useAppSelector((s) => s.spatial.placements.find((p) => p.cameraId === cameraId && p.isReference))
  const [calibration, setCalibration] = useState<PtzGeoCalibrationDto | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [aim, setAim] = useState<PtzGeoAimResultDto | null>(null)

  const profileToken = camera?.streams?.[0]?.profileToken ?? ''
  const stream = (camera?.streams ?? []).find((s) => s.webRtc && s.whepUrl && !s.disabled)

  const reload = useCallback(async () => {
    try {
      setCalibration(await spatialRequest<PtzGeoCalibrationDto>(`/api/ptz-geo/${encodeURIComponent(cameraId)}`))
    } catch (e) {
      setError(errorText(e))
    }
  }, [cameraId])

  useEffect(() => { void reload() }, [reload])

  // A slave would turn back to its master's objects between two recordings.
  useEffect(() => {
    const hold = (seconds: number) =>
      fetch(`/api/ptz-slave-binding/${encodeURIComponent(cameraId)}/hold`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ seconds }),
      }).catch(() => undefined)
    void hold(HOLD_SECONDS)
    const timer = window.setInterval(() => void hold(HOLD_SECONDS), HOLD_RENEW_MS)
    return () => {
      window.clearInterval(timer)
      void hold(0)
    }
  }, [cameraId])

  const observations = useMemo(() => calibration?.observations ?? [], [calibration])
  const fitted = calibration?.fitted === true
  const observationOf = (landmarkId: string) => observations.find((o) => o.landmarkId === landmarkId)

  const save = async (next: PtzGeoObservationDto[]) => {
    await spatialRequest(`/api/ptz-geo/${encodeURIComponent(cameraId)}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ observations: next.map(({ landmarkId, pan, tilt, zoom }) => ({ landmarkId, pan, tilt, zoom })) }),
    })
    await reload()
  }

  const act = async (action: () => Promise<void>) => {
    setBusy(true)
    setError(null)
    try {
      await action()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  const readStatus = async () => {
    const status = await spatialRequest<PtzStatusDto>(
      `/api/ptz/status?cameraId=${encodeURIComponent(cameraId)}&profileToken=${encodeURIComponent(profileToken)}`)
    if (!status.available) throw new Error(status.error || t('ptzGeo.noStatus'))
    return status
  }

  /** Replaces one landmark's observation, keeping the rest as they are. */
  const replace = (landmarkId: string, observation: PtzGeoObservationDto | null) =>
    save([...observations.filter((o) => o.landmarkId !== landmarkId), ...(observation ? [observation] : [])])

  // A new pan and tilt keep the zoom sample: the landmark is as far away as it was.
  const record = (landmark: LandmarkDto) => act(async () => {
    const status = await readStatus()
    setAim(null)
    await replace(landmark.id!, { landmarkId: landmark.id!, pan: status.pan, tilt: status.tilt, zoom: observationOf(landmark.id!)?.zoom })
  })

  const recordZoom = (observation: PtzGeoObservationDto) => act(async () => {
    const status = await readStatus()
    await replace(observation.landmarkId, { ...observation, zoom: status.zoom })
  })

  const forgetZoom = (observation: PtzGeoObservationDto) =>
    act(() => replace(observation.landmarkId, { ...observation, zoom: null }))

  const forget = (landmarkId: string) => act(() => replace(landmarkId, null))

  /**
   * Turns the camera to a landmark: by the calibration once there is one, with the zoom its samples
   * give; until then to where the landmark was recorded, at the zoom the camera has. A landmark
   * never recorded and no calibration yet leave the camera where it is, to be driven by hand.
   */
  const goTo = (landmark: LandmarkDto) => act(async () => {
    setAim(null)
    if (fitted) {
      setAim(await spatialRequest<PtzGeoAimResultDto>(`/api/ptz-geo/${encodeURIComponent(cameraId)}/aim`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ spaceId: landmark.spaceId, lat: landmark.lat, lon: landmark.lon, heightM: landmark.heightM }),
      }))
      return
    }
    const observed = landmark.id ? observationOf(landmark.id) : undefined
    if (!observed) return
    const status = await readStatus()
    await spatialRequest(`/api/ptz/absolute-move`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ cameraId, profileToken, pan: observed.pan, tilt: observed.tilt, zoom: status.zoom, speed: 0.5 }),
    })
  })

  const pick = (landmark: LandmarkDto) => {
    setSelectedId(landmark.id ?? null)
    void goTo(landmark)
  }

  const bounds = useMemo(() => {
    const pts: [number, number][] = landmarks.map((m) => [m.lat, m.lon])
    if (placement) pts.push([placement.lat, placement.lon])
    return pts.length >= 2 ? L.latLngBounds(pts) : null
  }, [landmarks, placement])

  const zooms = observations.flatMap((o) => (o.zoom != null ? [o.zoom] : []))

  return (
    <Dialog open onClose={onClose} maxWidth="xl" fullWidth>
      <DialogTitle>{t('ptzGeo.title', { name: camera?.name || cameraId })}</DialogTitle>
      <DialogContent sx={{ display: 'flex', gap: 2, height: '75vh' }}>
        <Stack spacing={1} sx={{ flex: 3, minWidth: 0 }}>
          <Box sx={{ position: 'relative', flex: 1, minHeight: 0, bgcolor: '#000', borderRadius: 1, overflow: 'hidden' }}>
            {stream?.whepUrl
              ? <CameraStream whepUrl={stream.whepUrl} stunUrl={camera?.effectiveWebrtcStunUrl} />
              : <Typography color="text.secondary" sx={{ p: 2 }}>{t('ptzGeo.noVideo')}</Typography>}
            <AimCrosshair />
          </Box>
          <Stack direction="row" spacing={2} alignItems="center">
            <PtzControlPad cameraId={cameraId} profileToken={profileToken} />
            <Stack spacing={0.5}>
              <Typography variant="body2" color="text.secondary">{t('ptzGeo.howTo')}</Typography>
              <Typography variant="body2" color="text.secondary">{t('ptzGeo.zoomHowTo')}</Typography>
            </Stack>
          </Stack>
        </Stack>

        <Stack spacing={1} sx={{ flex: 2, minWidth: 360 }}>
          <Box sx={{ height: 260, display: 'flex', borderRadius: 1, overflow: 'hidden' }}>
            <SpaceCanvas space={space} initialBounds={bounds}>
              {space.plan && <PlanOverlay space={space} />}
              {placement && (
                <CameraMarker placement={placement} label={camera?.name || cameraId} color={STATUS_COLOR.live} />
              )}
              {landmarks.map((m) => (
                <LandmarkMarker key={m.id} landmark={m} selected={m.id === selectedId} onSelect={() => !busy && pick(m)} />
              ))}
            </SpaceCanvas>
          </Box>

          {calibration && (
            fitted
              ? <Alert severity="success">{t('ptzGeo.fitted')}</Alert>
              : <Alert severity="warning">{calibration.error || t('ptzGeo.needTwo')}</Alert>
          )}
          {calibration && (
            <Typography variant="body2" color="text.secondary">
              {zooms.length > 0
                ? t('ptzGeo.zoomRange', { count: zooms.length, min: fmt(Math.min(...zooms)), max: fmt(Math.max(...zooms)) })
                : t('ptzGeo.noZoom')}
            </Typography>
          )}
          {error && <Alert severity="error" onClose={() => setError(null)}>{error}</Alert>}
          {aim && (
            <Alert severity={aim.extrapolated ? 'warning' : 'info'} onClose={() => setAim(null)}>
              {t('ptzGeo.aimed', {
                pan: fmt(aim.pan), tilt: fmt(aim.tilt), zoom: fmt(aim.zoom),
                az: aim.azimuthDeg.toFixed(1), el: aim.elevationDeg.toFixed(1), dist: aim.distanceM.toFixed(0),
              })}
              {aim.extrapolated && ` ${t('ptzGeo.extrapolated')}`}
            </Alert>
          )}

          <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
            {landmarks.length === 0 ? (
              <Typography variant="body2" color="text.secondary">{t('ptzGeo.noLandmarks')}</Typography>
            ) : (
              <Table size="small" stickyHeader>
                <TableHead>
                  <TableRow>
                    <TableCell>{t('maps.landmark')}</TableCell>
                    <TableCell align="right">{t('ptzGeo.distance')}</TableCell>
                    <TableCell align="right">{t('ptzGeo.pan')}</TableCell>
                    <TableCell align="right">{t('ptzGeo.tilt')}</TableCell>
                    <TableCell align="right">
                      <Tooltip title={t('ptzGeo.residualHint')}><span>{t('ptzGeo.residual')}</span></Tooltip>
                    </TableCell>
                    <TableCell align="right">
                      <Tooltip title={t('ptzGeo.zoomHint')}><span>{t('ptzGeo.zoom')}</span></Tooltip>
                    </TableCell>
                    <TableCell />
                  </TableRow>
                </TableHead>
                <TableBody>
                  {landmarks.map((m) => {
                    const o = m.id ? observationOf(m.id) : undefined
                    const residual = o && (o.panResidual != null || o.tiltResidual != null)
                      ? `${fmt(o.panResidual)} / ${fmt(o.tiltResidual)}`
                      : '—'
                    return (
                      <TableRow key={m.id} hover selected={m.id === selectedId} onClick={() => !busy && pick(m)} sx={{ cursor: 'pointer' }}>
                        <TableCell>{m.name}</TableCell>
                        <TableCell align="right">{o?.distanceM != null ? `${o.distanceM.toFixed(0)} m` : '—'}</TableCell>
                        <TableCell align="right">{fmt(o?.pan)}</TableCell>
                        <TableCell align="right">{fmt(o?.tilt)}</TableCell>
                        <TableCell align="right">{residual}</TableCell>
                        <TableCell align="right" sx={{ whiteSpace: 'nowrap' }} onClick={(e) => e.stopPropagation()}>
                          {fmt(o?.zoom)}
                          <Tooltip title={t('ptzGeo.recordZoom')}>
                            <span><IconButton size="small" disabled={busy || !o} onClick={() => o && void recordZoom(o)}><ZoomInIcon fontSize="small" /></IconButton></span>
                          </Tooltip>
                          <Tooltip title={t('ptzGeo.forgetZoom')}>
                            <span><IconButton size="small" disabled={busy || o?.zoom == null} onClick={() => o && void forgetZoom(o)}><ClearIcon fontSize="small" /></IconButton></span>
                          </Tooltip>
                        </TableCell>
                        <TableCell align="right" sx={{ whiteSpace: 'nowrap' }} onClick={(e) => e.stopPropagation()}>
                          <Tooltip title={t('ptzGeo.record')}>
                            <span><IconButton size="small" disabled={busy} onClick={() => void record(m)}><MyLocationIcon fontSize="small" /></IconButton></span>
                          </Tooltip>
                          <Tooltip title={t('ptzGeo.forget')}>
                            <span><IconButton size="small" disabled={busy || !o} onClick={() => m.id && void forget(m.id)}><DeleteOutlineIcon fontSize="small" /></IconButton></span>
                          </Tooltip>
                        </TableCell>
                      </TableRow>
                    )
                  })}
                </TableBody>
              </Table>
            )}
          </Box>
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>{t('common.close')}</Button>
      </DialogActions>
    </Dialog>
  )
}
