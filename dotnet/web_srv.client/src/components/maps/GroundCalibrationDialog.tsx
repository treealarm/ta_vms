import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Alert, Box, Button, Dialog, DialogActions, DialogContent, DialogTitle, IconButton, Stack, Table,
  TableBody, TableCell, TableHead, TableRow, Tooltip, Typography,
} from '@mui/material'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import UndoIcon from '@mui/icons-material/Undo'
import { CircleMarker, Polyline } from 'react-leaflet'
import L from 'leaflet'
import { useTranslation } from 'react-i18next'
import type { GroundCalibrationDto, GroundObservationDto, LandmarkDto, SpaceDto } from '../../dto/dtos'
import { useAppSelector } from '../../store'
import { errorText, spatialRequest } from '../../store/spatialSlice'
import CameraStream from '../CameraStream'
import MasterPointOverlay, { type MasterPoint } from '../bbox/MasterPointOverlay'
import SpaceCanvas from '../../map/SpaceCanvas'
import PlanOverlay from '../../map/PlanOverlay'
import { CameraMarker, LandmarkMarker } from '../../map/markers'
import { CameraDetections } from '../../map/DetectionsLayer'
import { STATUS_COLOR } from '../../map/cameraStatus'
import { project } from '../../map/groundProjection'
import { useUndoShortcut } from '../../hooks/useUndoShortcut'

type Props = {
  cameraId: string
  /** Where the camera really stands: its reference placement is in this space. */
  space: SpaceDto
  /** The landmarks of that space. */
  landmarks: LandmarkDto[]
  onClose: () => void
}

const fmtM = (v: number | null | undefined) => (v === null || v === undefined ? '—' : `${v.toFixed(2)} m`)

/**
 * Maps a fixed camera's picture onto the ground: pick a landmark, click where it stands in the
 * picture, repeat. Four make a mapping; from the fifth each one shows how far the mapping puts it.
 * Once there is a mapping, the pointer over the picture and the live detections show on the map
 * beside it, which is the check that the mapping is right.
 */
export default function GroundCalibrationDialog({ cameraId, space, landmarks, onClose }: Props) {
  const { t } = useTranslation()
  const camera = useAppSelector((s) => s.live.cameraStatesById[cameraId])
  const placement = useAppSelector((s) => s.spatial.placements.find((p) => p.cameraId === cameraId && p.isReference))
  const [calibration, setCalibration] = useState<GroundCalibrationDto | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  // A mark being dragged, shown where it is until it is let go and saved.
  const [moving, setMoving] = useState<{ index: number; x: number; y: number } | null>(null)
  // The same, for the handler that saves it: the drag ends on an event, not on a render.
  const movingRef = useRef<{ index: number; x: number; y: number } | null>(null)
  const [hover, setHover] = useState<{ x: number; y: number } | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // The marks before each edit since the dialog opened. Every edit is saved at once, so this is
  // the way back from a slipped drag or a click on the wrong spot.
  const [history, setHistory] = useState<GroundObservationDto[][]>([])

  const stream = (camera?.streams ?? []).find((s) => s.webRtc && s.whepUrl && !s.disabled)

  const reload = useCallback(async () => {
    try {
      setCalibration(await spatialRequest<GroundCalibrationDto>(`/api/ground-geo/${encodeURIComponent(cameraId)}`))
    } catch (e) {
      setError(errorText(e))
    }
  }, [cameraId])

  useEffect(() => { void reload() }, [reload])

  const observations = useMemo(() => calibration?.observations ?? [], [calibration])
  const matrix = calibration?.fitted ? calibration.matrix ?? null : null
  const landmarkById = useMemo(() => new Map(landmarks.map((m) => [m.id, m])), [landmarks])

  /** Writes the marks; an undo writes the earlier ones back and takes them off the history. */
  const save = async (next: GroundObservationDto[], undoing = false) => {
    setBusy(true)
    setError(null)
    const before = observations
    try {
      await spatialRequest(`/api/ground-geo/${encodeURIComponent(cameraId)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ observations: next.map(({ landmarkId, x, y }) => ({ landmarkId, x, y })) }),
      })
      setHistory((h) => (undoing ? h.slice(0, -1) : [...h, before].slice(-50)))
      await reload()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  const mark = (landmarkId: string, x: number, y: number) => {
    const exists = observations.some((o) => o.landmarkId === landmarkId)
    const next = exists
      ? observations.map((o) => (o.landmarkId === landmarkId ? { ...o, x, y } : o))
      : [...observations, { landmarkId, x, y }]
    // On to the next landmark not yet marked, so a round of marking is click, click, click.
    const marked = new Set(next.map((o) => o.landmarkId))
    setSelectedId(landmarks.find((m) => m.id && !marked.has(m.id))?.id ?? landmarkId)
    void save(next)
  }

  const undo = () => {
    if (busy || history.length === 0) return
    void save(history[history.length - 1], true)
  }
  useUndoShortcut(undo)

  const onPick = (x: number, y: number) => {
    if (!selectedId) {
      setError(t('ground.pickLandmarkFirst'))
      return
    }
    mark(selectedId, x, y)
  }

  // Numbered by the landmark's row in the table, which does not change as marks come and go.
  const numberOf = (landmarkId: string) => String(landmarks.findIndex((m) => m.id === landmarkId) + 1)
  const points: MasterPoint[] = observations.map((o, i) => ({
    ...(moving?.index === i ? { masterX: moving.x, masterY: moving.y } : { masterX: o.x, masterY: o.y }),
    label: numberOf(o.landmarkId),
  }))
  const selectedIndex = observations.findIndex((o) => o.landmarkId === selectedId)

  const bounds = useMemo(() => {
    const pts: [number, number][] = landmarks.map((m) => [m.lat, m.lon])
    if (placement) pts.push([placement.lat, placement.lon])
    return pts.length >= 2 ? L.latLngBounds(pts) : null
  }, [landmarks, placement])

  const hoverAt = matrix && hover ? project(matrix, hover.x, hover.y) : null

  return (
    <Dialog open onClose={onClose} maxWidth="xl" fullWidth>
      <DialogTitle>{t('ground.title', { name: camera?.name || cameraId })}</DialogTitle>
      <DialogContent sx={{ display: 'flex', gap: 2, height: '75vh' }}>
        <Stack spacing={1} sx={{ flex: 3, minWidth: 0 }}>
          <Box sx={{ position: 'relative', flex: 1, minHeight: 0, bgcolor: '#000', borderRadius: 1, overflow: 'hidden' }}>
            {stream?.whepUrl
              ? (
                <CameraStream
                  whepUrl={stream.whepUrl}
                  stunUrl={camera?.effectiveWebrtcStunUrl}
                  cameraId={cameraId}
                  bboxEnabled={!!matrix}
                  overlay={(videoRef) => (
                    <MasterPointOverlay
                      videoRef={videoRef}
                      points={points}
                      selectedIndex={selectedIndex >= 0 ? selectedIndex : null}
                      onPick={onPick}
                      onSelectPoint={(i) => setSelectedId(observations[i]?.landmarkId ?? null)}
                      onMovePoint={(index, x, y) => {
                        movingRef.current = { index, x, y }
                        setMoving(movingRef.current)
                      }}
                      onCommitPoint={() => {
                        const moved = movingRef.current
                        movingRef.current = null
                        setMoving(null)
                        const o = moved && observations[moved.index]
                        if (moved && o) void save(observations.map((p) => (p === o ? { ...p, x: moved.x, y: moved.y } : p)))
                      }}
                      onHover={setHover}
                    />
                  )}
                />
              )
              : <Typography color="text.secondary" sx={{ p: 2 }}>{t('ptzGeo.noVideo')}</Typography>}
          </Box>
          <Typography variant="body2" color="text.secondary">{t('ground.howTo')}</Typography>
        </Stack>

        <Stack spacing={1} sx={{ flex: 2, minWidth: 360 }}>
          <Box sx={{ height: 280, display: 'flex', borderRadius: 1, overflow: 'hidden' }}>
            <SpaceCanvas space={space} initialBounds={bounds}>
              {space.plan && <PlanOverlay space={space} />}
              {placement && (
                <CameraMarker placement={placement} label={camera?.name || cameraId} color={STATUS_COLOR.live} />
              )}
              {landmarks.map((m) => (
                <LandmarkMarker key={m.id} landmark={m} selected={m.id === selectedId} onSelect={() => setSelectedId(m.id ?? null)} />
              ))}
              {/* Where the mapping puts each mark: a line from the landmark to it is the miss, to scale. */}
              {matrix && observations.map((o) => {
                const at = project(matrix, o.x, o.y)
                const m = landmarkById.get(o.landmarkId)
                return at && m ? (
                  <Polyline
                    key={o.landmarkId}
                    positions={[[m.lat, m.lon], [at.lat, at.lon]]}
                    pathOptions={{ color: '#ef5350', weight: 2, dashArray: '4 4' }}
                    interactive={false}
                  />
                ) : null
              })}
              {hoverAt && (
                <CircleMarker
                  center={[hoverAt.lat, hoverAt.lon]}
                  radius={5}
                  pathOptions={{ color: '#ffca28', weight: 2, fillOpacity: 0 }}
                  interactive={false}
                />
              )}
              {matrix && (
                <CameraDetections cameraId={cameraId} matrix={matrix} reference={placement} label={camera?.name || cameraId} />
              )}
            </SpaceCanvas>
          </Box>

          {calibration && (
            calibration.fitted
              ? <Alert severity="success">{t('ground.fitted')}</Alert>
              : (
                <Alert severity="warning">
                  {calibration.error || t('ground.needFour')}
                  {observations.length > 0 && <> {t('ground.keptHint')}</>}
                </Alert>
              )
          )}
          {error && <Alert severity="error" onClose={() => setError(null)}>{error}</Alert>}

          <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
            {landmarks.length === 0 ? (
              <Typography variant="body2" color="text.secondary">{t('ptzGeo.noLandmarks')}</Typography>
            ) : (
              <Table size="small" stickyHeader>
                <TableHead>
                  <TableRow>
                    <TableCell>#</TableCell>
                    <TableCell>{t('maps.landmark')}</TableCell>
                    <TableCell align="right">
                      <Tooltip title={t('ground.residualHint')}><span>{t('ground.residual')}</span></Tooltip>
                    </TableCell>
                    <TableCell align="right">
                      <Tooltip title={t('common.undoLast')}>
                        <span>
                          <IconButton size="small" disabled={busy || history.length === 0} onClick={undo}>
                            <UndoIcon fontSize="small" />
                          </IconButton>
                        </span>
                      </Tooltip>
                    </TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {landmarks.map((m, row) => {
                    const o = observations.find((p) => p.landmarkId === m.id)
                    return (
                      <TableRow key={m.id} hover selected={m.id === selectedId} onClick={() => setSelectedId(m.id ?? null)} sx={{ cursor: 'pointer' }}>
                        <TableCell sx={{ color: o ? undefined : 'text.disabled' }}>{row + 1}</TableCell>
                        <TableCell>{m.name}</TableCell>
                        <TableCell align="right">{fmtM(o?.residualM)}</TableCell>
                        <TableCell align="right">
                          <Tooltip title={t('ground.forget')}>
                            <span>
                              <IconButton
                                size="small"
                                disabled={busy || !o}
                                onClick={(e) => {
                                  e.stopPropagation()
                                  void save(observations.filter((p) => p.landmarkId !== m.id))
                                }}
                              >
                                <DeleteOutlineIcon fontSize="small" />
                              </IconButton>
                            </span>
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
