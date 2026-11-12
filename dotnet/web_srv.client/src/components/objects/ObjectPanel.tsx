import { useEffect, useState, type ReactNode } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  Alert, Box, Button, Chip, Divider, IconButton, Stack, ToggleButton, Tooltip, Typography,
} from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import AccountTreeIcon from '@mui/icons-material/AccountTree'
import CenterFocusStrongIcon from '@mui/icons-material/CenterFocusStrong'
import GpsFixedIcon from '@mui/icons-material/GpsFixed'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import LoginIcon from '@mui/icons-material/Login'
import { useTranslation } from 'react-i18next'
import type { MapExtraProperty } from '../../dto/mapDtos'
import type { ControlObjectDto, PtzGeoAimResultDto } from '../../dto/dtos'
import { getControlObject } from '../../api/controlApi'
import { useAppDispatch, useAppSelector } from '../../store'
import { loadNodes, loadObjProps } from '../../store/objectsSlice'
import { loadObjectEvents } from '../../store/eventsSlice'
import CameraUnifiedCard from '../CameraUnifiedCard'
import ObjectControl from '../control/ObjectControl'
import NodeIdentity from './NodeIdentity'
import NodeStateChips from './NodeStateChips'

type Props = {
  nodeId: string
  onClose: () => void
  onSelect: (id: string) => void
  /** Enter a node that holds children: the canvas zooms into it, or a space opens its own. */
  onEnter?: (id: string) => void
  /** Find the node on the canvas that draws it: its space or diagram is opened and it is centred. */
  onLocate?: (id: string) => void
  /** Show the node in the tree beside the map: the path down to it is unfolded and scrolled to. */
  onShowInTree?: (id: string) => void
  /** The diagram drawn for this camera by its template, if the deployment has one. */
  onOpenDiagram?: (id: string) => void
  /** Selects a camera by id: what the "show device" link of a diagram's panel does. */
  onSelectCamera?: (cameraId: string) => void
  /** Map aiming: a camera with telemetry can be pointed at a place on the canvas. */
  aiming: boolean
  onAimingChange: (on: boolean) => void
  aimUnavailable: string | null
  aimResult: PtzGeoAimResultDto | null
  aimError: string | null
}

/**
 * The right-hand panel of the operator map: a camera opens the unified card — live, archive, PTZ,
 * guard, recording — while any other node shows its properties, states and latest events, read
 * only. The camera itself and its capability to be aimed come from the map: the panel only draws.
 */
export default function ObjectPanel({
  nodeId, onClose, onSelect, onEnter, onLocate, onShowInTree, onOpenDiagram, onSelectCamera,
  aiming, onAimingChange, aimUnavailable, aimResult, aimError,
}: Props) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const navigate = useNavigate()
  const marker = useAppSelector((s) => s.objects.markers[nodeId])
  const props = useAppSelector((s) => s.objects.props[nodeId])
  const state = useAppSelector((s) => s.states.states[nodeId])
  const alarm = useAppSelector((s) => s.states.alarms[nodeId])
  const descriptions = useAppSelector((s) => s.states.descriptions)
  const events = useAppSelector((s) => s.events.byObject[nodeId])
  const camera = useAppSelector((s) => {
    const cameraId = s.objects.markers[nodeId]?.cameraId
    return cameraId ? s.live.cameraStatesById[cameraId] : undefined
  })
  // Where the node is, when it is anywhere: its own figure on some canvas, or — for a camera node,
  // which is never drawn itself — a placement of the camera. Naming a place needs one.
  const figure = useAppSelector((s) => s.objects.figures[nodeId])
  const placements = useAppSelector((s) => s.spatial.placements)
  const drawn = !!figure
    || !!(marker?.cameraId && placements.some((placement) => placement.cameraId === marker.cameraId))

  useEffect(() => {
    void dispatch(loadObjProps(nodeId))
    void dispatch(loadObjectEvents(nodeId))
  }, [nodeId, dispatch])

  // A node selected on the canvas may arrive before the tree has read it.
  useEffect(() => {
    if (!marker) void dispatch(loadNodes([nodeId]))
  }, [marker, nodeId, dispatch])

  const kind = marker?.nodeKind ?? 'object'
  const childrenAlarms = alarm?.childrenAlarms ?? 0
  const states = state?.states ?? []

  // A device part — a sensor or a relay, canonical or the replica the operator clicked — answers
  // with the same control row the Control panel shows, resolved by the server through its owner.
  // A camera needs none of this: its card above already arms it, and a second switch would be two
  // ways to say one thing.
  const [control, setControl] = useState<{ nodeId: string; row: ControlObjectDto | null } | null>(null)
  const [controlNonce, setControlNonce] = useState(0)
  useEffect(() => {
    if (kind !== 'part') return

    let cancelled = false
    void getControlObject(nodeId)
      .then((row) => { if (!cancelled) setControl({ nodeId, row }) })
      .catch(() => { if (!cancelled) setControl({ nodeId, row: null }) })
    return () => { cancelled = true }
  }, [kind, nodeId, controlNonce])

  // The answer must be this node's: a part selected before keeps its row until the next one lands.
  const controlRow = kind === 'part' && control?.nodeId === nodeId ? control.row : null

  return (
    <Stack sx={{ width: 420, flexShrink: 0, borderLeft: 1, borderColor: 'divider', minHeight: 0 }}>
      <Stack
        direction="row"
        alignItems="center"
        spacing={1}
        sx={{ px: 1.5, py: 1, borderBottom: 1, borderColor: 'divider', flexShrink: 0 }}
      >
        <Box sx={{ flex: 1, minWidth: 0 }}>
          <Typography variant="subtitle1" fontWeight={600} noWrap>
            {marker?.name || nodeId}
          </Typography>
          <Typography variant="caption" color="text.secondary">
            {t(`maps.node.kind.${kind}`)}
          </Typography>
        </Box>
        {onEnter && marker && (marker.hasChildren || kind === 'space' || kind === 'diagram') ? (
          <Tooltip title={t('maps.node.enterHint')}>
            <Button size="small" startIcon={<LoginIcon fontSize="small" />} onClick={() => onEnter(nodeId)}>
              {t('maps.node.enter')}
            </Button>
          </Tooltip>
        ) : null}
        {onLocate && drawn ? (
          <Tooltip title={t('maps.node.locate')}>
            <IconButton size="small" aria-label={t('maps.node.locate')} onClick={() => onLocate(nodeId)}>
              <CenterFocusStrongIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        ) : null}
        {/* The other way round: the node was picked on the canvas, the tree is where it stands
            among its neighbours. */}
        {onShowInTree ? (
          <Tooltip title={t('maps.node.showInTree')}>
            <IconButton
              size="small"
              aria-label={t('maps.node.showInTree')}
              onClick={() => onShowInTree(nodeId)}
            >
              <AccountTreeIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        ) : null}
        {onOpenDiagram && kind === 'camera' ? (
          <Tooltip title={t('diagramEditor.openDeviceDiagram')}>
            <Button
              size="small"
              startIcon={<OpenInNewIcon fontSize="small" />}
              onClick={() => onOpenDiagram(nodeId)}
            >
              {t('diagramEditor.deviceDiagram')}
            </Button>
          </Tooltip>
        ) : null}
        <Button
          size="small"
          onClick={() => navigate(`/operator/events?node=${encodeURIComponent(nodeId)}`)}
        >
          {t('maps.node.journal')}
        </Button>
        <IconButton size="small" onClick={onClose}><CloseIcon fontSize="small" /></IconButton>
      </Stack>

      <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', p: 1.5 }}>
        {/* What the node is, before what it reports: a picture on a plan is a replica, and the
            panel says which node it shows; a diagram says which device it draws. */}
        {marker ? (
          <Box sx={{ mb: 1.5 }}>
            <NodeIdentity
              node={marker}
              device={props?.device}
              onSelect={onSelect}
              onSelectCamera={onSelectCamera}
            />
          </Box>
        ) : null}
        {camera ? (
          <>
            <Box sx={{ height: 340, flexShrink: 0, mb: 1 }}>
              <CameraUnifiedCard camera={camera} slotKey={`map:${camera.cameraId}`} />
            </Box>
            {camera.hasTelemetry ? (
              <Stack spacing={0.75} sx={{ mb: 1.5 }}>
                <Tooltip title={aimUnavailable ?? t('maps.aimHint')}>
                  <span>
                    <ToggleButton
                      size="small"
                      value="aim"
                      fullWidth
                      disabled={!!aimUnavailable}
                      selected={aiming}
                      onChange={() => onAimingChange(!aiming)}
                    >
                      <GpsFixedIcon fontSize="small" sx={{ mr: 1 }} />{t('maps.aimHere')}
                    </ToggleButton>
                  </span>
                </Tooltip>
                {aimUnavailable ? (
                  <Typography variant="caption" color="text.secondary">{aimUnavailable}</Typography>
                ) : null}
                {aimError ? <Alert severity="error">{aimError}</Alert> : null}
                {aimResult ? (
                  <Alert severity={aimResult.extrapolated ? 'warning' : 'success'}>
                    {t('maps.aimed', { dist: aimResult.distanceM.toFixed(0), az: aimResult.azimuthDeg.toFixed(0) })}
                    {aimResult.extrapolated ? ` ${t('ptzGeo.extrapolated')}` : ''}
                  </Alert>
                ) : null}
              </Stack>
            ) : null}
          </>
        ) : marker?.cameraId ? (
          <Alert severity="info" sx={{ mb: 1.5 }}>{t('maps.node.cameraMissing')}</Alert>
        ) : null}

        {controlRow && (
          <Section title={t('control.title')}>
            <ObjectControl
              row={controlRow}
              onChanged={() => setControlNonce((n) => n + 1)}
            />
          </Section>
        )}

        <Section title={t('maps.node.states')}>
          <Stack direction="row" spacing={0.5} flexWrap="wrap" useFlexGap>
            {childrenAlarms > 0 ? (
              <Chip
                size="small"
                variant="outlined"
                color="error"
                label={t('maps.node.childAlarms', { count: childrenAlarms })}
              />
            ) : null}
            <NodeStateChips state={state} alarm={alarm} descriptions={descriptions} />
            {!alarm?.alarm && childrenAlarms === 0 && states.length === 0 ? (
              <Typography variant="body2" color="text.secondary">{t('maps.node.noStates')}</Typography>
            ) : null}
          </Stack>
        </Section>

        <Section title={t('maps.node.properties')}>
          {props?.extraProps?.length ? (
            props.extraProps.map((prop) => <PropertyRow key={prop.propName} prop={prop} />)
          ) : (
            <Typography variant="body2" color="text.secondary">{t('maps.node.noProperties')}</Typography>
          )}
          {props?.visual?.color ? (
            <PropertyRow prop={{ propName: t('maps.node.color'), strVal: props.visual.color, visualType: '__clr' }} />
          ) : null}
          {props?.visual?.text ? (
            <PropertyRow prop={{ propName: t('maps.node.caption'), strVal: props.visual.text }} />
          ) : null}
        </Section>

        <Section title={t('maps.node.lastEvents')}>
          {events?.length ? (
            events.map((event) => (
              <Stack key={event.id ?? event.timestamp} direction="row" spacing={1} sx={{ py: 0.15 }}>
                <Typography variant="caption" color="text.secondary" sx={{ whiteSpace: 'nowrap' }}>
                  {formatTime(event.timestamp)}
                </Typography>
                <Typography variant="body2" noWrap>
                  {[event.eventName, event.param0, event.param1].filter(Boolean).join(' · ')}
                </Typography>
              </Stack>
            ))
          ) : (
            <Typography variant="body2" color="text.secondary">{t('maps.node.noEvents')}</Typography>
          )}
        </Section>
      </Box>
    </Stack>
  )
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <Box sx={{ mb: 1.5 }}>
      <Typography variant="overline" color="text.secondary" sx={{ display: 'block', lineHeight: 1.6 }}>
        {title}
      </Typography>
      <Divider sx={{ mb: 0.75 }} />
      {children}
    </Box>
  )
}

function PropertyRow({ prop }: { prop: MapExtraProperty }) {
  const raw = prop.strVal ?? ''
  const isColor = prop.visualType === '__clr'
  const text = prop.visualType === '__datetime' ? formatTime(raw) : raw

  return (
    <Stack direction="row" spacing={1} alignItems="flex-start" sx={{ py: 0.15 }}>
      <Typography variant="body2" color="text.secondary" sx={{ minWidth: 110, flexShrink: 0 }}>
        {prop.propName}
      </Typography>
      {isColor ? (
        <Box sx={{ width: 14, height: 14, borderRadius: 0.5, bgcolor: raw, mt: 0.25, flexShrink: 0 }} />
      ) : null}
      <Typography variant="body2" sx={{ wordBreak: 'break-word' }}>{text}</Typography>
    </Stack>
  )
}

function formatTime(iso?: string): string {
  if (!iso) return ''
  const date = new Date(iso)
  return Number.isNaN(date.getTime()) ? iso : date.toLocaleString()
}
