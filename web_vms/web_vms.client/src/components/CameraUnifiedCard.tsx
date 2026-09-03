import { useEffect, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { patchSlot, selectSlotUi } from '../store/monitorUiSlice'
import {
  alpha,
  Box,
  Button,
  Card,
  Chip,
  Divider,
  IconButton,
  Stack,
  ToggleButton,
  ToggleButtonGroup,
  Tooltip,
  Typography,
  Alert,
  Snackbar,
} from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import ContentCopyIcon from '@mui/icons-material/ContentCopy'
import FiberManualRecordIcon from '@mui/icons-material/FiberManualRecord'
import FullscreenIcon from '@mui/icons-material/Fullscreen'
import FullscreenExitIcon from '@mui/icons-material/FullscreenExit'
import LayersIcon from '@mui/icons-material/Layers'
import VideocamIcon from '@mui/icons-material/Videocam'
import TuneIcon from '@mui/icons-material/Tune'
import VideoLibraryIcon from '@mui/icons-material/VideoLibrary'
import ControlCameraIcon from '@mui/icons-material/ControlCamera'
import SensorsIcon from '@mui/icons-material/Sensors'
import DirectionsRunIcon from '@mui/icons-material/DirectionsRun'
import SecurityIcon from '@mui/icons-material/Security'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import SpeedIcon from '@mui/icons-material/Speed'
import {
  effectiveStreamMode,
  type MonitorKind,
  recordableStreams,
  streamSize,
  type CameraSetupDto,
  type CameraStreamDto,
} from '../dto/dtos'
import { useAdaptiveStream } from '../hooks/useAdaptiveStream'
import { formatStreamParams } from '../utils/streamParams'
import SeamlessCameraStream from './SeamlessCameraStream'
import ArchiveHlsStreamCard from './ArchiveHlsStreamCard'
import ArchiveWallPlayer from './wall/ArchiveWallPlayer'
import StreamOverrideRow from './StreamOverrideRow'
import StreamProfileRow from './StreamProfileRow'
import PtzOverlay from './PtzOverlay'
import GpsFixedIcon from '@mui/icons-material/GpsFixed'
import { setPtzTracking, usePtzTracking } from '../hooks/usePtzTracking'
import DeviceIoOverlay from './DeviceIoOverlay'
import { useOnvifBboxCapable } from '../hooks/useOnvifBboxCapable'
import { useArchiveMetadataCoverage } from '../hooks/archiveMetadataClient'
import { wallPlaylistRequestBounds } from '../utils/playlistUrl'
import { selectWallSeekMs } from '../store/wallPlaybackSlice'
import { useAppDispatch, useAppSelector } from '../store'
import { fetchCameraState, setArmedWithPrealarmBaseline, setCameraContinuousRecording } from '../store/liveSlice'
import { setMonitorSlotStream } from '../store/monitorsSlice'
import { copyToClipboard } from '../utils/copyToClipboard'
import type { DrawableBox } from './bbox/bboxDraw'

const STRIP_W = 28
const PANEL_W = 182

const W = '#fff'   // common.white shorthand for video overlays
const B = '#000'   // common.black shorthand for video overlays

// netTerrainTheme panel.elevated — keep in sync with theme/netTerrainTheme.ts
const STRIP_BG = { bgcolor: '#434343', backdropFilter: 'blur(6px)' } as const

const BTN_SX = {
  p: '3px',
  color: alpha(W, 0.8),
  borderRadius: '4px',
  '&:hover': { color: W, bgcolor: alpha(W, 0.12) },
} as const

// Separates the control groups in the compact strip (cell / device / camera state).
const GROUP_DIVIDER_SX = {
  width: '65%',
  borderColor: alpha(W, 0.2),
  my: 0.25,
} as const

const CAPTION_SX = {
  display: 'block',
  fontSize: '0.6rem',
  color: alpha(W, 0.45),
  textTransform: 'uppercase' as const,
  letterSpacing: '0.06em',
  mb: 0.4,
}

type Props = {
  camera: CameraSetupDto
  monitorKind?: MonitorKind
  slotKey?: string
  /** Set when the card sits in a monitor wall; enables wall-wide playback. */
  monitorId?: string
  /** Which slot of that wall. Together with monitorId it is what makes the pinned stream a stored,
   *  per-operator preference rather than something lost on reload. */
  slotIndex?: number
  /** Wall-wide coverage query inputs, shared by every cell so it is fetched once. */
  wallCameraIds?: readonly string[]
  coverageWindowStartMs?: number
  coverageWindowWidthMs?: number
  /**
   * Wall-level mode. While it is 'playback' the cell follows the wall's shared clock and the
   * per-cell live/archive mode below is only suspended, not overwritten — it comes back on exit.
   */
  wallMode?: 'live' | 'playback'
  gridSoloActive?: boolean
  onToggleGridSolo?: () => void
}

export default function CameraUnifiedCard({
  camera,
  monitorKind = 'wall',
  slotKey,
  monitorId,
  slotIndex,
  wallCameraIds,
  coverageWindowStartMs,
  coverageWindowWidthMs,
  wallMode = 'live',
  gridSoloActive = false,
  onToggleGridSolo,
}: Props) {
  const dispatch = useAppDispatch()
  const navigate = useNavigate()
  const cameraState = useAppSelector((s) => s.live.cameraStatesById[camera.cameraId])
  // Hidden whenever Square integration isn't configured — keeps standalone mode unchanged.
  const squareFrontendOrigin = useAppSelector((s) => s.frontendConfig.squareFrontendOrigin)

  // UI state — persisted in Redux when slotKey provided, local otherwise
  const storeKey = slotKey ?? `standalone-${camera.cameraId}`
  const slotUi = useAppSelector(selectSlotUi(storeKey))

  const mode = slotUi.mode
  const wallPlayback = wallMode === 'playback' && !!monitorId

  // On a monitor the pin is stored per operator and survives a reload; a standalone card has no
  // slot to store it against and keeps it in local UI state.
  const pinIsPersisted = monitorId !== undefined && slotIndex !== undefined
  const persistedPin = useAppSelector((st) =>
    pinIsPersisted
      ? st.monitors.monitors
          .find((m) => m.monitorId === monitorId)?.slots
          .find((x) => x.slotIndex === slotIndex)?.streamName ?? ''
      : ''
  )
  const selectedStream = pinIsPersisted ? persistedPin : slotUi.selectedStream
  const panelOpen = slotUi.panelOpen
  const ptzOpen = slotUi.ptzOpen
  const deviceIoOpen = slotUi.deviceIoOpen
  const bboxOpen = slotUi.bboxOpen
  const statsOpen = slotUi.statsOpen

  const setMode = (v: 'live' | 'archive') => dispatch(patchSlot({ key: storeKey, mode: v }))
  const setSelectedStream = (v: string) => {
    if (pinIsPersisted) void dispatch(setMonitorSlotStream({ monitorId, slotIndex, streamName: v }))
    else dispatch(patchSlot({ key: storeKey, selectedStream: v }))
  }
  const setPanelOpen = (v: boolean) => dispatch(patchSlot({ key: storeKey, panelOpen: v }))
  const setBboxOpen = (v: boolean) => dispatch(patchSlot({ key: storeKey, bboxOpen: v }))
  const setStatsOpen = (v: boolean) => dispatch(patchSlot({ key: storeKey, statsOpen: v }))
  // PTZ and Device I/O overlays are mutually exclusive so they never render on top of each other.
  const setPtzOpen = (v: boolean) => dispatch(patchSlot({ key: storeKey, ptzOpen: v, ...(v ? { deviceIoOpen: false } : {}) }))
  const setDeviceIoOpen = (v: boolean) => dispatch(patchSlot({ key: storeKey, deviceIoOpen: v, ...(v ? { ptzOpen: false } : {}) }))

  const { t } = useTranslation()

  // Observed by the adaptive stream chooser: its CSS size is what the picture has to suit.
  const videoAreaRef = useRef<HTMLDivElement>(null)

  const [onvifRtsp, setOnvifRtsp] = useState('')
  const [pendingArmed, setPendingArmed] = useState<boolean | null>(null)
  const [copied, setCopied] = useState(false)
  const [similarError, setSimilarError] = useState<string | null>(null)
  const [trackingError, setTrackingError] = useState<string | null>(null)

  const merged: CameraSetupDto = { ...camera, ...(cameraState ?? {}) }

  const isDisabled = merged.disabled === true
  const effectiveArmed = pendingArmed !== null ? pendingArmed : (merged.armed ?? false)
  // An open recording schedule window forces continuous recording on every stream, ahead of the
  // profile and of any manual or alarm override.
  const scheduleActive = merged.scheduleActive === true
  const streamMode = (s: CameraStreamDto) => effectiveStreamMode(s, scheduleActive)
  // The resolution rides on the chip because it is what explains the automatic choice: without it
  // an operator sees the tile switch streams and has nothing to read it against.
  const chipLabel = (s: CameraStreamDto) => {
    const size = streamSize(s)
    return size ? `${s.streamName} ${size.width}×${size.height}` : s.streamName
  }
  const armDisarm = (enabled: boolean) => {
    if (isDisabled) return
    setPendingArmed(enabled)
    void dispatch(setArmedWithPrealarmBaseline({ cameraId: merged.cameraId, armed: enabled }))
      .finally(() => setPendingArmed(null))
  }

  const recordingMode = merged.recordingMode ?? 'off'
  const webRtcStreams = (merged.streams ?? []).filter((s) => s.webRtc)
  // A disabled stream has no reader behind it and no WHEP url, so it can be listed but never
  // chosen automatically.
  const liveCandidates = webRtcStreams.filter((s) => !s.disabled)

  // An explicit choice by the operator; empty means Auto. Auto is the default, and it is the only
  // state in which the slot is allowed to change stream by itself.
  const pinnedStream = selectedStream || null
  const autoStream = useAdaptiveStream(
    videoAreaRef,
    liveCandidates,
    mode === 'live' && !wallPlayback && pinnedStream === null,
  )
  const activeStream = pinnedStream ?? autoStream ?? webRtcStreams[0]?.streamName ?? ''
  const activeStreamObj =
    (merged.streams ?? []).find((s) => s.streamName === activeStream) ??
    (merged.streams ?? [])[0] ??
    null
  const activeRetention = merged.activeRetentionWindow === true && recordingMode !== 'off'
  // Retention means "this footage is being kept", which is not the same as "this camera is
  // recording" — and a red frame is read as the latter by anyone who has used a camera system.
  // Recording owns red; retention gets amber below.
  //
  // The recordingMode check comes first on purpose. It used to sit after the continuous branch,
  // so a stream whose profile said continuous lit the frame even with the camera's recording
  // switched off — the frame claimed a recording that was not happening.
  const isStreamRetained = (s: CameraStreamDto) => {
    if (streamMode(s) === 'off' || recordingMode === 'off') return false
    if (streamMode(s) === 'continuous') return true
    return merged.allStreamsRetained === true || (merged.retainedStreams ?? []).includes(s.streamName)
  }
  const anyStreamRetained = (merged.streams ?? []).some(isStreamRetained)
  // The record button acts on the CAMERA — every enabled stream available for recording — not on
  // whichever stream the tile happens to be showing. With adaptive live quality that would often be
  // the sub-stream, so the button would archive the wrong thing.
  const archivableStreams = recordableStreams(merged)
  const hasArchivableStreams = archivableStreams.length > 0
  // Reads like "select all": on only when every recordable stream is already recording continuously.
  const cameraContinuous =
    hasArchivableStreams && archivableStreams.every((s) => s.recordingProfile?.mode === 'continuous')

  const [pendingContinuous, setPendingContinuous] = useState(false)
  const [recordingError, setRecordingError] = useState<string | null>(null)
  const toggleContinuousRecording = async () => {
    // A schedule window outranks the profile, so toggling it would have no visible effect.
    if (!hasArchivableStreams || isDisabled || scheduleActive) return
    const turningOn = !cameraContinuous

    setPendingContinuous(true)
    try {
      // Ask the file service first, and only when switching on: it is the one that knows whether
      // a storage is assigned, in service, and actually writable. Without this the click succeeds,
      // the profile changes, and nothing is ever written — which is what the operator then has to
      // discover by noticing an absence.
      if (turningOn) {
        const r = await fetch(`/api/live/cameras/${encodeURIComponent(merged.cameraId)}/recording-readiness`)
        if (r.ok) {
          const readiness: { ready: boolean; reason: string; storageId: string; detail: string } = await r.json()
          if (!readiness.ready) {
            setRecordingError(t(`cameraCard.notReady.${readiness.reason}`, {
              defaultValue: t('cameraCard.notReady.unknown'),
              storage: readiness.storageId,
              detail: readiness.detail,
            }))
            return
          }
        }
      }
      // One camera-level call rather than a loop: the server picks the streams, so the UI cannot
      // drift from what the "available for recording" flag allows, and turning recording off lands
      // on pre-alarm or off according to the armed state without a second round trip.
      await dispatch(setCameraContinuousRecording({ cameraId: merged.cameraId, on: turningOn }))
    } finally {
      setPendingContinuous(false)
    }
  }

  const configuredRtsp = camera.path || ''
  useEffect(() => {
    if (configuredRtsp || !camera.onvifConn) return
    const token = activeStreamObj?.profileToken ?? ''
    const qs = token ? `?profileToken=${encodeURIComponent(token)}` : ''
    fetch(`/api/onvif/cameras/${encodeURIComponent(camera.cameraId)}/stream-uri${qs}`)
      .then((r) => (r.ok ? (r.json() as Promise<{ rtspUrl: string }>) : Promise.reject()))
      .then((d) => setOnvifRtsp(d.rtspUrl || ''))
      .catch(() => setOnvifRtsp(''))
    // Same trap as the capabilities effect below: camera.onvifConn is a fresh object reference
    // on every cameraState poll, so depending on its identity refires this every 5s — each
    // refire being a full ONVIF SOAP round-trip to the physical camera via vmsonvif.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [camera.cameraId, Boolean(camera.onvifConn), configuredRtsp, activeStreamObj?.profileToken])

  const rtsp = configuredRtsp || (camera.onvifConn ? onvifRtsp : '')

  // Device I/O is still gated on what the camera reports: relays and inputs either answer or they
  // do not, and there is nothing an operator could know that the device does not. PTZ is the
  // exception — see hasTelemetry below.
  const [hasRelayOutputs, setHasRelayOutputs] = useState<boolean | null>(null)
  const [hasDigitalInputs, setHasDigitalInputs] = useState<boolean | null>(null)
  useEffect(() => {
    if (!camera.onvifConn) { setHasRelayOutputs(null); setHasDigitalInputs(null); return }
    fetch(`/api/onvif/cameras/${encodeURIComponent(camera.cameraId)}/capabilities`)
      .then((r) => (r.ok
        ? (r.json() as Promise<{ hasRelayOutputs: boolean; hasDigitalInputs: boolean }>)
        : Promise.reject()))
      .then((d) => { setHasRelayOutputs(d.hasRelayOutputs); setHasDigitalInputs(d.hasDigitalInputs) })
      .catch(() => { setHasRelayOutputs(null); setHasDigitalInputs(null) }) // unknown -> fail open, same as the ONVIF settings panel
    // camera.onvifConn is a fresh object reference on every cameraState poll (see the
    // fetchCameraState interval below) even when unchanged — depend on its presence, not its
    // identity, so this doesn't refetch every 5s.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [camera.cameraId, Boolean(camera.onvifConn)])

  // Whether this camera can be driven at all. Deliberately the configured flag and not the ONVIF
  // capability: firmware advertises the PTZ service whether or not a motor was ever fitted, so the
  // probe puts a joystick on fixed cameras. The camera's ONVIF settings page still shows what it
  // claims — that is where you check one before ticking the flag.
  const hasTelemetry = camera.hasTelemetry === true
  useEffect(() => { if (!hasTelemetry && ptzOpen) setPtzOpen(false) }, [hasTelemetry, ptzOpen])
  useEffect(() => {
    if (hasRelayOutputs === false && hasDigitalInputs === false && deviceIoOpen) setDeviceIoOpen(false)
  }, [hasRelayOutputs, hasDigitalInputs, deviceIoOpen])
  // Live-only controls: in wall playback the cell shows recorded video, so PTZ and device I/O have
  // nothing to act on.
  const showPtzControl = hasTelemetry && mode === 'live' && !wallPlayback && !isDisabled
  // Automatic tracking rides on the same flag as the joystick: both drive the motor, and neither
  // has anything to act on while the cell is showing recorded video.
  const showTrackingControl = showPtzControl
  const tracking = usePtzTracking(showTrackingControl ? camera.cameraId : undefined)
  const trackingActive = tracking?.active === true

  const toggleTracking = async () => {
    try {
      await setPtzTracking(camera.cameraId, !trackingActive)
    } catch (e) {
      setTrackingError(e instanceof Error ? e.message : String(e))
    }
  }

  // Picking an object from the overlay both arms the mode and names the target, so one click does
  // what the operator meant rather than needing the button pressed first.
  const trackObject = async (box: DrawableBox) => {
    try {
      await setPtzTracking(camera.cameraId, true, {
        source: box.source,
        trackId: box.trackId,
        kind: box.kind,
      })
    } catch (e) {
      setTrackingError(e instanceof Error ? e.message : String(e))
    }
  }
  const showDeviceIoControl = Boolean(camera.onvifConn) && mode === 'live' && !wallPlayback && !isDisabled
    && ((hasRelayOutputs ?? true) || (hasDigitalInputs ?? true))

  // The controls are grouped by what they act on, and each group only appears when it has
  // something to show — otherwise the separators between them would dangle.
  //
  //   1. cell    — changes what THIS tile displays, never touches the camera
  //   2. device  — drives the physical camera right now, so it needs live video
  //   3. state   — writes camera configuration and doubles as its indicator
  //
  // In wall playback the per-cell live/archive toggle is suspended (the wall owns the mode).
  const showModeToggle = !wallPlayback
  const isLiveContext = mode === 'live' && !wallPlayback
  // Two independent box sources feed the same overlay: the CV detector (a watch configured in
  // admin, reported on the camera) and the camera's own ONVIF Scene Description (configured on the
  // device itself, so it has to be probed — see useOnvifBboxCapable). Either one is reason enough
  // to offer the toggle.
  const onvifBboxCapable = useOnvifBboxCapable(camera.cameraId, isLiveContext && !isDisabled)
  const hasBoxSource = Boolean(camera.cvWatchEnabled) || onvifBboxCapable

  // In an archive context the live test is the wrong question. It describes the camera *now*, while
  // the operator may be looking at a period before the watch existed — or after it was deleted.
  // Whether boxes exist is a property of (camera, stream, time range), so it is asked of the
  // archive over whatever window is currently loaded.
  const archiveStreamName =
    activeStreamObj?.streamName || (merged.streams ?? [])[0]?.streamName || ''
  const [cardArchiveBounds, setCardArchiveBounds] = useState<{ startMs: number; endMs: number } | null>(null)
  // For the wall the window is derivable without asking the player: it is built from the same
  // anchor the playlist URL is.
  const wallSeekMs = useAppSelector(selectWallSeekMs(monitorId ?? ''))
  const wallBounds = useMemo(
    () => (wallPlayback ? wallPlaylistRequestBounds(wallSeekMs) : null),
    [wallPlayback, wallSeekMs]
  )
  const archiveBounds = wallPlayback ? wallBounds : cardArchiveBounds
  const archiveCoverage = useArchiveMetadataCoverage(
    !isLiveContext && !isDisabled ? camera.cameraId : undefined,
    archiveStreamName,
    archiveBounds?.startMs ?? null,
    archiveBounds?.endMs ?? null
  )
  // Hidden only on a definitive "nothing recorded here"; while the answer is still outstanding the
  // control is offered, because a button that appears late is worse than one that appears and turns
  // out to have nothing behind it.
  //
  // Deliberately NOT falling back to hasBoxSource here: onvifBboxCapable is probed only in a live
  // context, so in the archive that test collapses to camera.cvWatchEnabled — which is false for a
  // camera whose boxes come solely from its own ONVIF Scene Description. Gating on it hid the
  // button outright for exactly the cameras this feature was built for.
  const archiveHasBoxes = archiveCoverage !== 'none'
  const showBboxToggle = !isDisabled && (isLiveContext ? hasBoxSource : archiveHasBoxes)
  // Recording profile and override act on configuration, which is meaningful whatever the tile is
  // currently showing — but in the standalone card they follow the card's own mode.
  const showStateConfigRows = wallPlayback || mode === 'live'

  const hasCellGroup = onToggleGridSolo != null || showModeToggle || webRtcStreams.length > 1
    || showBboxToggle || Boolean(squareFrontendOrigin)
  const hasDeviceGroup = showPtzControl || showDeviceIoControl

  // A poll that has not come back yet must not have another stacked on top of it. Every card runs
  // this, and a browser allows six connections per origin — so two cards whose polls each take
  // longer than the interval are enough to spend the whole budget on requests for the same answer,
  // leaving nothing for live video or anything else on the page.
  const statePollBusy = useRef(false)
  useEffect(() => {
    let cancelled = false

    const poll = async () => {
      if (statePollBusy.current) return
      statePollBusy.current = true
      try {
        await dispatch(fetchCameraState(camera.cameraId))
      } finally {
        statePollBusy.current = false
      }
    }

    void poll()
    const id = setInterval(() => { if (!cancelled) void poll() }, 5000)
    return () => {
      cancelled = true
      clearInterval(id)
      statePollBusy.current = false
    }
  }, [camera.cameraId, dispatch])

  // Auto is part of the cycle, not a separate control: the compact rail has one button, and an
  // operator who pinned a stream to look at something needs a way back to automatic without
  // opening the panel. Order: Auto -> first -> ... -> last -> Auto.
  const cycleStream = () => {
    if (webRtcStreams.length < 2) return
    if (pinnedStream === null) {
      setSelectedStream(webRtcStreams[0].streamName)
      return
    }
    const idx = webRtcStreams.findIndex((s) => s.streamName === pinnedStream)
    const next = webRtcStreams[idx + 1]
    setSelectedStream(next ? next.streamName : '')
  }

  // What the tile is actually showing. During a swap the chosen stream is already the new one while
  // the picture is still the old one, so anything describing the picture reads from here.
  const [shownWhepUrl, setShownWhepUrl] = useState<string | null>(null)
  const shownStreamObj =
    (merged.streams ?? []).find((s) => s.whepUrl && s.whepUrl === shownWhepUrl) ?? activeStreamObj
  const searchableKinds = useMemo(
    () => new Set(['person', 'face', 'vehicle', 'license_plate']),
    []
  )

  const openFindSimilar = (box: DrawableBox) => {
    if (!searchableKinds.has(box.kind)) {
      setSimilarError(t('forensic.kindUnavailable'))
      return
    }
    const probeTs = Number.isFinite(box.timeMs) ? new Date(box.timeMs).toISOString() : new Date().toISOString()
    const params = new URLSearchParams({
      cameraId: camera.cameraId,
      trackId: String(box.trackId),
      ts: probeTs,
      kind: box.kind,
      from: monitorKind === 'investigation' ? 'investigations' : 'monitors',
    })
    if (monitorId) params.set('monitorId', monitorId)
    // Absolute pathname+search so RR7 splat relative resolution cannot rewrite this into a
    // nested monitors/... path.
    navigate({ pathname: '/operator/object-findings', search: `?${params.toString()}` })
  }

  // Per-stream settings need an unambiguous stream. With one candidate Auto is unambiguous, so it
  // is only a lock when there is actually something to choose between.
  const streamSettingsLocked = pinnedStream === null && webRtcStreams.length > 1

  const whepUrl = activeStreamObj?.whepUrl

  return (
    <Card
      sx={{
        height: '100%', width: '100%', overflow: 'hidden',
        display: 'flex', flexDirection: 'row',
        // Red, solid: this camera is recording. Amber, dashed: it is not recording, but footage
        // it already has is being retained. Two different facts, two different marks.
        ...(recordingMode !== 'off'
          ? {
              outline: (t) => `2px solid ${t.palette.error.main}`,
              outlineOffset: '-2px',
              ...(activeRetention
                ? { boxShadow: (t) => `0 0 8px ${t.palette.error.main}66` }
                : {}),
            }
          : anyStreamRetained || activeRetention
            ? {
                outline: (t) => `2px dashed ${t.palette.warning.main}`,
                outlineOffset: '-2px',
              }
            : {}),
      }}
    >
      {/* ── Left: icon strip (compact) ── */}
      {!panelOpen && (
        <Stack
          alignItems="center"
          spacing={0.25}
          sx={{ ...STRIP_BG, width: STRIP_W, flexShrink: 0, py: 0.5, zIndex: 1 }}
        >
          {/* ── Group 1: the cell ── */}
          {onToggleGridSolo != null && (
            <Tooltip title={gridSoloActive ? t('cameraCard.showAll') : t('cameraCard.expand')} placement="right">
              <IconButton size="small" onClick={onToggleGridSolo} sx={BTN_SX}>
                {gridSoloActive
                  ? <FullscreenExitIcon sx={{ fontSize: '0.95rem' }} />
                  : <FullscreenIcon sx={{ fontSize: '0.95rem' }} />}
              </IconButton>
            </Tooltip>
          )}

          {showModeToggle && (
            <Tooltip title={mode === 'live' ? t('cameraCard.switchToArchive') : t('cameraCard.switchToLive')} placement="right">
              <IconButton size="small" onClick={() => setMode(mode === 'live' ? 'archive' : 'live')} sx={BTN_SX}>
                {mode === 'live'
                  ? <VideoLibraryIcon sx={{ fontSize: '0.95rem' }} />
                  : <VideocamIcon sx={{ fontSize: '0.95rem' }} />}
              </IconButton>
            </Tooltip>
          )}

          {webRtcStreams.length > 1 && (
            <Tooltip
              title={t('cameraCard.cycleStream', {
                stream: pinnedStream ?? `${t('cameraCard.autoStream')} (${activeStream})`,
              })}
              placement="right"
            >
              <IconButton size="small" onClick={cycleStream} sx={BTN_SX}>
                <LayersIcon sx={{ fontSize: '0.95rem' }} />
              </IconButton>
            </Tooltip>
          )}

          {/* Analytics bbox overlay — keyed by camera, not stream: works even when the monitor
              shows a sub-stream that has no analytics of its own (it mirrors the main stream).
              Only offered when the camera actually has an enabled analytics watch. */}
          {showBboxToggle && (
            <Tooltip title={bboxOpen ? t('cameraCard.bboxClose') : t('cameraCard.bboxOpen')} placement="right">
              <IconButton
                size="small"
                onClick={() => setBboxOpen(!bboxOpen)}
                sx={{ ...BTN_SX, ...(bboxOpen ? { color: W, bgcolor: alpha(W, 0.2) } : {}) }}
              >
                <DirectionsRunIcon sx={{ fontSize: '0.95rem' }} />
              </IconButton>
            </Tooltip>
          )}

          {/* Stream data overlay. Sits with the other overlay toggles because that is what it is —
              it draws on the video, it does not change anything about the stream. */}
          {mode === 'live' && (
            <Tooltip title={statsOpen ? t('cameraCard.streamStatsClose') : t('cameraCard.streamStatsOpen')} placement="right">
              <IconButton
                size="small"
                onClick={() => setStatsOpen(!statsOpen)}
                sx={{ ...BTN_SX, ...(statsOpen ? { color: W, bgcolor: alpha(W, 0.2) } : {}) }}
              >
                <SpeedIcon sx={{ fontSize: '0.95rem' }} />
              </IconButton>
            </Tooltip>
          )}

          {squareFrontendOrigin && (
            <Tooltip title={t('cameraCard.openInSquare')} placement="right">
              <IconButton
                size="small"
                onClick={() => window.open(`${squareFrontendOrigin}/?selectedId=${encodeURIComponent(camera.cameraId)}`, '_blank')}
                sx={BTN_SX}
              >
                <OpenInNewIcon sx={{ fontSize: '0.95rem' }} />
              </IconButton>
            </Tooltip>
          )}

          {/* ── Group 2: live control of the physical device ── */}
          {hasDeviceGroup && hasCellGroup && <Divider sx={GROUP_DIVIDER_SX} />}

          {showPtzControl && (
            <Tooltip title={ptzOpen ? t('cameraCard.ptzClose') : t('cameraCard.ptzOpen')} placement="right">
              <IconButton
                size="small"
                onClick={() => setPtzOpen(!ptzOpen)}
                sx={{ ...BTN_SX, ...(ptzOpen ? { color: W, bgcolor: alpha(W, 0.2) } : {}) }}
              >
                <ControlCameraIcon sx={{ fontSize: '0.95rem' }} />
              </IconButton>
            </Tooltip>
          )}

          {showTrackingControl && (
            <Tooltip
              title={trackingActive ? t('tracking.stopTooltip') : t('tracking.startTooltip')}
              placement="right"
            >
              <IconButton
                size="small"
                onClick={() => void toggleTracking()}
                sx={{ ...BTN_SX, ...(trackingActive ? { color: W, bgcolor: alpha(W, 0.2) } : {}) }}
              >
                <GpsFixedIcon sx={{ fontSize: '0.95rem' }} />
              </IconButton>
            </Tooltip>
          )}

          {showDeviceIoControl && (
            <Tooltip title={deviceIoOpen ? t('cameraCard.deviceIoClose') : t('cameraCard.deviceIoOpen')} placement="right">
              <IconButton
                size="small"
                onClick={() => setDeviceIoOpen(!deviceIoOpen)}
                sx={{ ...BTN_SX, ...(deviceIoOpen ? { color: W, bgcolor: alpha(W, 0.2) } : {}) }}
              >
                <SensorsIcon sx={{ fontSize: '0.95rem' }} />
              </IconButton>
            </Tooltip>
          )}

          {/* ── Group 3: camera state — each button both performs and indicates ── */}
          {(hasCellGroup || hasDeviceGroup) && <Divider sx={GROUP_DIVIDER_SX} />}

          {/* Armed gates both ONVIF events and video analytics now, so it's available for every
              camera — not just ONVIF-managed ones. */}
          <Tooltip
            title={isDisabled ? t('cameraCard.cameraDisabledTip') : (effectiveArmed ? t('cameraCard.armedTip') : t('cameraCard.disarmedTip'))}
            placement="right"
          >
            <span>
              <IconButton
                size="small"
                disabled={pendingArmed !== null || isDisabled}
                onClick={() => armDisarm(!effectiveArmed)}
                sx={{
                  ...BTN_SX,
                  ...(effectiveArmed && !isDisabled ? { color: '#ff9800' } : {}),
                  ...((pendingArmed !== null || isDisabled) ? { opacity: 0.35 } : {}),
                }}
              >
                <SecurityIcon sx={{ fontSize: '0.95rem' }} />
              </IconButton>
            </span>
          </Tooltip>

          {/* Always rendered, even with nothing to record: hiding it reads as "this camera cannot
              be recorded at all", so it stays put and greys out with a tooltip that says why. */}
          <Tooltip
            title={
              !hasArchivableStreams
                ? t('cameraCard.noRecordableStreams')
                : scheduleActive
                  ? t('cameraCard.scheduleActive')
                  : cameraContinuous ? t('cameraCard.continuousOn') : t('cameraCard.continuousOff')
            }
            placement="right"
          >
            <span>
              <IconButton
                size="small"
                disabled={pendingContinuous || isDisabled || scheduleActive || !hasArchivableStreams}
                onClick={() => void toggleContinuousRecording()}
                sx={{
                  ...BTN_SX,
                  ...(scheduleActive || cameraContinuous ? { color: 'error.main' } : {}),
                  ...((pendingContinuous || isDisabled || !hasArchivableStreams) ? { opacity: 0.35 } : {}),
                }}
              >
                <FiberManualRecordIcon sx={{ fontSize: '0.95rem' }} />
              </IconButton>
            </span>
          </Tooltip>

          <Box sx={{ flex: 1 }} />

          <Tooltip title={t('cameraCard.controls')} placement="right">
            <IconButton size="small" onClick={() => setPanelOpen(true)} sx={BTN_SX}>
              <TuneIcon sx={{ fontSize: '0.95rem' }} />
            </IconButton>
          </Tooltip>
        </Stack>
      )}

      {/* ── Left: expanded panel ── */}
      {panelOpen && (
        <Stack
          sx={{ ...STRIP_BG, width: PANEL_W, flexShrink: 0, overflow: 'hidden auto', zIndex: 1 }}
        >
          {/* Header */}
          <Stack direction="row" alignItems="center" sx={{ px: 1, pt: 0.75, pb: 0.5, flexShrink: 0 }}>
            <Typography
              variant="body2"
              sx={{ flex: 1, color: W, fontWeight: 600, fontSize: '0.8rem', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
            >
              {camera.name || camera.cameraId}
            </Typography>
            {showPtzControl && (
              <Tooltip title={ptzOpen ? t('cameraCard.ptzClose') : t('cameraCard.ptzOpen')}>
                <IconButton
                  size="small"
                  onClick={() => setPtzOpen(!ptzOpen)}
                  sx={{ p: '2px', color: ptzOpen ? W : alpha(W, 0.5), '&:hover': { color: W } }}
                >
                  <ControlCameraIcon sx={{ fontSize: '0.9rem' }} />
                </IconButton>
              </Tooltip>
            )}
            {showDeviceIoControl && (
              <Tooltip title={deviceIoOpen ? t('cameraCard.deviceIoClose') : t('cameraCard.deviceIoOpen')}>
                <IconButton
                  size="small"
                  onClick={() => setDeviceIoOpen(!deviceIoOpen)}
                  sx={{ p: '2px', color: deviceIoOpen ? W : alpha(W, 0.5), '&:hover': { color: W } }}
                >
                  <SensorsIcon sx={{ fontSize: '0.9rem' }} />
                </IconButton>
              </Tooltip>
            )}
            {rtsp && (
              <Tooltip title={copied ? t('cameraCard.copiedRtsp') : t('cameraCard.copyRtsp')}>
                <IconButton
                  size="small"
                  sx={{ p: '2px', color: copied ? '#4caf50' : alpha(W, 0.5), '&:hover': { color: W } }}
                  onClick={() => {
                    void copyToClipboard(rtsp).then((ok) => {
                      if (!ok) return
                      setCopied(true)
                      setTimeout(() => setCopied(false), 1500)
                    })
                  }}
                >
                  <ContentCopyIcon sx={{ fontSize: '0.8rem' }} />
                </IconButton>
              </Tooltip>
            )}
            <IconButton
              size="small"
              onClick={() => setPanelOpen(false)}
              sx={{ p: '2px', color: alpha(W, 0.5), '&:hover': { color: W } }}
            >
              <CloseIcon sx={{ fontSize: '0.9rem' }} />
            </IconButton>
          </Stack>

          <Divider sx={{ borderColor: alpha(W, 0.12), flexShrink: 0 }} />

          <Stack spacing={1.25} sx={{ p: 1 }}>

            {/* Streams */}
            {webRtcStreams.length > 1 && (
              <Box>
                <Typography component="span" sx={CAPTION_SX}>{t('cameraCard.streamSection')}</Typography>
                <Stack direction="row" flexWrap="wrap" gap={0.5}>
                  {/* Auto is a chip like the others so that "no stream pinned" is a visible state
                      rather than the absence of one — otherwise automatic and a pin that happens to
                      match look identical. */}
                  <Tooltip title={t('cameraCard.autoStreamTip')}>
                    <Chip
                      label={pinnedStream === null && shownStreamObj
                        ? `${t('cameraCard.autoStream')} · ${shownStreamObj.streamName}`
                        : t('cameraCard.autoStream')}
                      size="small"
                      onClick={() => setSelectedStream('')}
                      variant={pinnedStream === null ? 'filled' : 'outlined'}
                      sx={{
                        height: 20, fontSize: '0.68rem', cursor: 'pointer',
                        color: pinnedStream === null ? B : alpha(W, 0.75),
                        bgcolor: pinnedStream === null ? alpha(W, 0.9) : 'transparent',
                        borderColor: alpha(W, 0.3),
                        '& .MuiChip-label': { px: 0.75 },
                      }}
                    />
                  </Tooltip>
                  {webRtcStreams.map((s) => (
                    <Tooltip
                      key={s.streamName}
                      title={
                        isStreamRetained(s) ? t('cameraCard.streamRecordingTip')
                        : streamSize(s) && !streamSize(s)!.observed ? t('cameraCard.declaredSize')
                        : ''
                      }
                    >
                      <Chip
                        label={chipLabel(s)}
                        size="small"
                        icon={isStreamRetained(s) ? <FiberManualRecordIcon /> : undefined}
                        onClick={() => setSelectedStream(s.streamName)}
                        variant={pinnedStream === s.streamName ? 'filled' : 'outlined'}
                        sx={{
                          height: 20, fontSize: '0.68rem', cursor: 'pointer',
                          color: pinnedStream === s.streamName ? B : alpha(W, 0.75),
                          bgcolor: pinnedStream === s.streamName ? alpha(W, 0.9) : 'transparent',
                          borderColor: isStreamRetained(s) ? 'error.main' : alpha(W, 0.3),
                          '& .MuiChip-label': { px: 0.75 },
                          '& .MuiChip-icon': { fontSize: '0.55rem', color: 'error.main', ml: 0.6 },
                        }}
                      />
                    </Tooltip>
                  ))}
                </Stack>
              </Box>
            )}

            {/* What the tile is actually receiving. Read-only, and the one place an operator can
                check whether the picture matches the stream they think they are watching. */}
            {mode === 'live' && activeStreamObj && (
              <Box>
                <Typography component="span" sx={CAPTION_SX}>{t('cameraCard.streamParamsSection')}</Typography>
                <Typography variant="caption" sx={{ display: 'block', color: alpha(W, 0.75), fontSize: '0.68rem' }}>
                  {shownStreamObj ? formatStreamParams(shownStreamObj) : '—'}
                </Typography>
              </Box>
            )}

            {/* Mode — suspended in wall playback, where the wall owns the mode */}
            {showModeToggle && (
              <Box>
                <Typography component="span" sx={CAPTION_SX}>{t('cameraCard.modeSection')}</Typography>
                <ToggleButtonGroup
                  value={mode} exclusive onChange={(_, v) => v && setMode(v)} size="small" fullWidth
                  sx={{
                    '& .MuiToggleButton-root': {
                      flex: 1, py: 0.25, fontSize: '0.7rem',
                      color: alpha(W, 0.6), borderColor: alpha(W, 0.2),
                      '&.Mui-selected': { color: W, bgcolor: alpha(W, 0.15), borderColor: alpha(W, 0.35) },
                    },
                  }}
                >
                  <ToggleButton value="live">{t('cameraCard.modeLive')}</ToggleButton>
                  <ToggleButton value="archive">{t('cameraCard.modeArchive')}</ToggleButton>
                </ToggleButtonGroup>
              </Box>
            )}

            {/* Solo — like the toggles above, this only rearranges the wall, not the camera */}
            {onToggleGridSolo != null && (
              <Button
                size="small"
                variant="outlined"
                startIcon={gridSoloActive ? <FullscreenExitIcon /> : <FullscreenIcon />}
                onClick={onToggleGridSolo}
                fullWidth
                sx={{
                  fontSize: '0.7rem',
                  color: alpha(W, 0.7), borderColor: alpha(W, 0.2),
                  '&:hover': { color: W, borderColor: alpha(W, 0.5), bgcolor: alpha(W, 0.07) },
                }}
              >
                {gridSoloActive ? t('cameraCard.showAll') : t('cameraCard.expand')}
              </Button>
            )}

            <Divider sx={{ borderColor: alpha(W, 0.12) }} />

            {/* ── Camera state: everything below writes camera configuration ── */}

            {/* Recording profile (base) */}
            {showStateConfigRows && activeStreamObj && (
              <Box>
                <Typography component="span" sx={CAPTION_SX}>
                  {webRtcStreams.length > 1
                    ? t('cameraCard.recordingSectionForStream', { stream: activeStream })
                    : t('cameraCard.recordingSection')}
                </Typography>
                {streamSettingsLocked ? (
                  // This setting belongs to one stream, so it has to be clear which. In Auto the
                  // shown stream changes with the tile's size, and an operator would be editing
                  // whichever one happened to be on screen at the time.
                  <Typography variant="caption" sx={{ display: 'block', color: alpha(W, 0.55), fontSize: '0.68rem' }}>
                    {t('cameraCard.pinStreamToEdit')}
                  </Typography>
                ) : (
                  <Stack direction="row" alignItems="center" spacing={1} sx={{ px: 0.25 }}>
                    <StreamProfileRow cameraId={camera.cameraId} stream={activeStreamObj} />
                  </Stack>
                )}
              </Box>
            )}

            {/* Override (automation) */}
            {showStateConfigRows && (
              <Box>
                <Typography component="span" sx={CAPTION_SX}>
                  {webRtcStreams.length > 1
                    ? t('cameraCard.overrideSectionForStream', { stream: activeStream })
                    : t('cameraCard.overrideSection')}
                </Typography>
                <StreamOverrideRow cameraId={camera.cameraId} streams={merged.streams ?? []} />
              </Box>
            )}

            {/* Guard — gates both ONVIF events and video analytics, so shown for every camera */}
            <Box>
              <Typography component="span" sx={CAPTION_SX}>
                {t('cameraCard.guardSection')} {t('cameraCard.cameraWide')}
              </Typography>
              <ToggleButtonGroup
                value={effectiveArmed ? 'armed' : 'disarmed'}
                exclusive
                disabled={pendingArmed !== null || isDisabled}
                onChange={(_, v) => v && armDisarm(v === 'armed')}
                size="small" fullWidth
                sx={{
                  '& .MuiToggleButton-root': {
                    flex: 1, py: 0.25, fontSize: '0.7rem',
                    color: alpha(W, 0.6), borderColor: alpha(W, 0.2),
                    '&.Mui-selected': { color: W, bgcolor: alpha(W, 0.15), borderColor: alpha(W, 0.35) },
                  },
                  '& .MuiToggleButton-root[value="armed"].Mui-selected': { color: '#ff9800', borderColor: '#ff9800' },
                }}
              >
                <ToggleButton value="armed">{t('cameraCard.armedToggle')}</ToggleButton>
                <ToggleButton value="disarmed">{t('cameraCard.disarmedToggle')}</ToggleButton>
              </ToggleButtonGroup>
            </Box>

            {/* RTSP copy */}
            {rtsp && (
              <Stack direction="row" alignItems="center" gap={0.5}>
                <Typography sx={{ flex: 1, fontSize: '0.62rem', color: alpha(W, 0.35), overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {rtsp}
                </Typography>
                <Tooltip title={copied ? t('cameraCard.copiedRtsp') : t('cameraCard.copyRtsp')}>
                  <IconButton
                    size="small"
                    sx={{ p: '2px', color: copied ? '#4caf50' : alpha(W, 0.45), flexShrink: 0, '&:hover': { color: W } }}
                    onClick={() => {
                      void copyToClipboard(rtsp).then((ok) => {
                        if (!ok) return
                        setCopied(true)
                        setTimeout(() => setCopied(false), 1500)
                      })
                    }}
                  >
                    <ContentCopyIcon sx={{ fontSize: '0.75rem' }} />
                  </IconButton>
                </Tooltip>
              </Stack>
            )}

            {/* Retention */}
            {activeRetention && (
              <Stack direction="row" alignItems="center" gap={0.75}>
                <FiberManualRecordIcon sx={{ fontSize: '0.6rem', color: 'error.main', flexShrink: 0 }} />
                <Typography sx={{ fontSize: '0.65rem', color: 'error.light' }}>{t('cameraCard.retentionActive')}</Typography>
              </Stack>
            )}
          </Stack>
        </Stack>
      )}

      {/* ── Right: video / archive ── fills remaining space */}
      <Box ref={videoAreaRef} sx={{ flex: 1, minWidth: 0, minHeight: 0, display: 'flex', overflow: 'hidden', position: 'relative' }}>
        {/* Stream data over the picture. Top-left, clear of the camera name at the bottom and of the
            PTZ / device-I/O grips; it never takes the pointer, so it cannot get between the
            operator and the video controls underneath. */}
        {statsOpen && mode === 'live' && (
          <Box
            sx={{
              position: 'absolute', top: 4, left: 4, zIndex: 3,
              bgcolor: alpha(B, 0.55), px: 0.75, py: 0.35, borderRadius: 0.5,
              pointerEvents: 'none', maxWidth: 'calc(100% - 8px)',
            }}
          >
            <Typography sx={{ fontSize: '0.65rem', color: W, lineHeight: 1.5, whiteSpace: 'nowrap' }}>
              {shownStreamObj?.streamName ?? '—'}
            </Typography>
            <Typography sx={{ fontSize: '0.65rem', color: alpha(W, 0.8), lineHeight: 1.5, whiteSpace: 'nowrap' }}>
              {shownStreamObj ? formatStreamParams(shownStreamObj) : '—'}
            </Typography>
          </Box>
        )}

        {!panelOpen && (
          <Typography
            sx={{
              position: 'absolute', bottom: 4, left: 4, zIndex: 2,
              fontSize: '0.65rem', color: alpha(W, 0.75),
              bgcolor: alpha(B, 0.45), px: 0.6, py: 0.15, borderRadius: 0.5,
              pointerEvents: 'none', lineHeight: 1.4,
              maxWidth: 'calc(100% - 8px)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}
          >
            {camera.name || camera.cameraId}
          </Typography>
        )}
        {wallPlayback ? (
          <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
            <ArchiveWallPlayer
              monitorId={monitorId!}
              slotKey={storeKey}
              cameraId={camera.cameraId}
              // Required, not optional: without a stream filter the preview playlist interleaves
              // main and sub clips into one overlapping source — the exact corruption the parameter
              // exists to prevent. Falls back to the camera's first stream.
              streamName={activeStreamObj?.streamName || (merged.streams ?? [])[0]?.streamName || ''}
              wallCameraIds={wallCameraIds ?? []}
              coverageWindowStartMs={coverageWindowStartMs ?? 0}
              coverageWindowWidthMs={coverageWindowWidthMs ?? 0}
              bboxEnabled={bboxOpen && archiveHasBoxes}
              onFindSimilar={openFindSimilar}
            />
          </Box>
        ) : (
          <>
            {mode === 'live' && whepUrl && (
              <SeamlessCameraStream
                whepUrl={whepUrl}
                stunUrl={camera.effectiveWebrtcStunUrl}
                cameraId={camera.cameraId}
                bboxEnabled={bboxOpen && hasBoxSource}
                onShownUrlChange={setShownWhepUrl}
                onFindSimilar={openFindSimilar}
                onTrackObject={showTrackingControl ? (box) => void trackObject(box) : undefined}
                trackingHighlight={tracking?.target ?? null}
              />
            )}
            {mode === 'archive' && (
              <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
                <ArchiveHlsStreamCard
                  cameraId={camera.cameraId}
                  bboxEnabled={bboxOpen && archiveHasBoxes}
                  onWallBoundsChange={setCardArchiveBounds}
                  onFindSimilar={openFindSimilar}
                />
              </Box>
            )}
          </>
        )}
        {showPtzControl && ptzOpen && (
          <PtzOverlay
            cameraId={camera.cameraId}
            profileToken={activeStreamObj?.profileToken ?? ''}
            onClose={() => setPtzOpen(false)}
          />
        )}
        {showDeviceIoControl && deviceIoOpen && (
          <DeviceIoOverlay
            cameraId={camera.cameraId}
            onClose={() => setDeviceIoOpen(false)}
          />
        )}
      </Box>
      <Snackbar
        open={recordingError !== null}
        onClose={() => setRecordingError(null)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
        sx={{ position: 'absolute' }}
      >
        <Alert severity="error" variant="filled" onClose={() => setRecordingError(null)}>
          {recordingError}
        </Alert>
      </Snackbar>
      <Snackbar
        open={trackingError !== null}
        onClose={() => setTrackingError(null)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
        sx={{ position: 'absolute' }}
      >
        <Alert severity="warning" variant="filled" onClose={() => setTrackingError(null)}>
          {trackingError}
        </Alert>
      </Snackbar>
      <Snackbar
        open={similarError !== null}
        onClose={() => setSimilarError(null)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
        sx={{ position: 'absolute' }}
      >
        <Alert severity="warning" variant="filled" onClose={() => setSimilarError(null)}>
          {similarError}
        </Alert>
      </Snackbar>
    </Card>
  )
}
