import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
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
} from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import ContentCopyIcon from '@mui/icons-material/ContentCopy'
import FiberManualRecordIcon from '@mui/icons-material/FiberManualRecord'
import FullscreenIcon from '@mui/icons-material/Fullscreen'
import FullscreenExitIcon from '@mui/icons-material/FullscreenExit'
import LayersIcon from '@mui/icons-material/Layers'
import LiveTvIcon from '@mui/icons-material/LiveTv'
import TuneIcon from '@mui/icons-material/Tune'
import VideoLibraryIcon from '@mui/icons-material/VideoLibrary'
import ControlCameraIcon from '@mui/icons-material/ControlCamera'
import SecurityIcon from '@mui/icons-material/Security'
import {
  RECORDING_PROFILE_CONTINUOUS_ID,
  RECORDING_PROFILE_OFF_ID,
  RECORDING_PROFILE_PREALARM_ID,
  type CameraSetupDto,
  type CameraStreamDto,
} from '../dto/dtos'
import CameraStream from './CameraStream'
import ArchiveHlsStreamCard from './ArchiveHlsStreamCard'
import StreamOverrideRow from './StreamOverrideRow'
import StreamProfileRow from './StreamProfileRow'
import PtzOverlay from './PtzOverlay'
import { useAppDispatch, useAppSelector } from '../store'
import { fetchCameraState, setArmed, setStreamOverride, setStreamRecordingProfile } from '../store/liveSlice'

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
  slotKey?: string
  gridSoloActive?: boolean
  onToggleGridSolo?: () => void
}

export default function CameraUnifiedCard({ camera, slotKey, gridSoloActive = false, onToggleGridSolo }: Props) {
  const dispatch = useAppDispatch()
  const cameraState = useAppSelector((s) => s.live.cameraStatesById[camera.cameraId])

  // UI state — persisted in Redux when slotKey provided, local otherwise
  const storeKey = slotKey ?? `standalone-${camera.cameraId}`
  const slotUi = useAppSelector(selectSlotUi(storeKey))

  const mode = slotUi.mode
  const selectedStream = slotUi.selectedStream
  const panelOpen = slotUi.panelOpen
  const ptzOpen = slotUi.ptzOpen

  const setMode = (v: 'live' | 'archive') => dispatch(patchSlot({ key: storeKey, mode: v }))
  const setSelectedStream = (v: string) => dispatch(patchSlot({ key: storeKey, selectedStream: v }))
  const setPanelOpen = (v: boolean) => dispatch(patchSlot({ key: storeKey, panelOpen: v }))
  const setPtzOpen = (v: boolean) => dispatch(patchSlot({ key: storeKey, ptzOpen: v }))

  const { t } = useTranslation()

  const [onvifRtsp, setOnvifRtsp] = useState('')
  const [pendingArmed, setPendingArmed] = useState<boolean | null>(null)
  const [copied, setCopied] = useState(false)

  const merged: CameraSetupDto = { ...camera, ...(cameraState ?? {}) }

  const isOnvifManaged = merged.onvifManaged ?? false
  const isDisabled = merged.disabled === true
  const effectiveArmed = pendingArmed !== null ? pendingArmed : (merged.armed ?? false)
  const effectiveStreamMode = (s: CameraStreamDto) => (s.overrideProfile ?? s.recordingProfile)?.mode ?? 'off'
  const isStreamRecording = (s: CameraStreamDto) => effectiveStreamMode(s) !== 'off'
  const armDisarm = (enabled: boolean) => {
    if (isDisabled) return
    setPendingArmed(enabled)
    const streams = merged.streams ?? []
    void dispatch(setArmed({ cameraId: merged.cameraId, armed: enabled }))
      .then(() => {
        for (const s of streams) {
          if (s.disabled) continue
          if (enabled) {
            if (!isStreamRecording(s)) {
              void dispatch(setStreamRecordingProfile({ cameraId: merged.cameraId, streamName: s.streamName, profileId: RECORDING_PROFILE_PREALARM_ID }))
            }
          } else if (s.recordingProfile?.id === RECORDING_PROFILE_PREALARM_ID) {
            void dispatch(setStreamRecordingProfile({ cameraId: merged.cameraId, streamName: s.streamName, profileId: RECORDING_PROFILE_OFF_ID }))
          }
        }
      })
      .finally(() => setPendingArmed(null))
  }

  const recordingMode = merged.recordingMode ?? 'off'
  const webRtcStreams = (merged.streams ?? []).filter((s) => s.webRtc)
  const activeStream = selectedStream || webRtcStreams[0]?.streamName || ''
  const activeStreamObj =
    (merged.streams ?? []).find((s) => s.streamName === activeStream) ??
    (merged.streams ?? [])[0] ??
    null
  const activeRetention = merged.activeRetentionWindow === true && recordingMode !== 'off'
  const anyStreamRecording = (merged.streams ?? []).some(isStreamRecording)
  const activeStreamContinuous = activeStreamObj?.recordingProfile?.mode === 'continuous'

  const [pendingContinuous, setPendingContinuous] = useState(false)
  const toggleContinuousRecording = () => {
    if (!activeStreamObj || isDisabled) return
    const profileId = activeStreamContinuous
      ? (effectiveArmed ? RECORDING_PROFILE_PREALARM_ID : RECORDING_PROFILE_OFF_ID)
      : RECORDING_PROFILE_CONTINUOUS_ID
    setPendingContinuous(true)
    void dispatch(setStreamRecordingProfile({ cameraId: merged.cameraId, streamName: activeStreamObj.streamName, profileId }))
      .finally(() => setPendingContinuous(false))
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
  }, [camera.cameraId, camera.onvifConn, configuredRtsp, activeStreamObj?.profileToken])

  const rtsp = configuredRtsp || (camera.onvifConn ? onvifRtsp : '')

  useEffect(() => {
    const id = setInterval(() => void dispatch(fetchCameraState(camera.cameraId)), 5000)
    void dispatch(fetchCameraState(camera.cameraId))
    return () => clearInterval(id)
  }, [camera.cameraId, dispatch])

  const cycleStream = () => {
    if (webRtcStreams.length < 2) return
    const idx = webRtcStreams.findIndex((s) => s.streamName === activeStream)
    setSelectedStream(webRtcStreams[(idx + 1) % webRtcStreams.length].streamName)
  }

  const streamName =
    webRtcStreams.length > 0 ? `${camera.cameraId}/${activeStream}` : camera.cameraId

  return (
    <Card
      sx={{
        height: '100%', width: '100%', overflow: 'hidden',
        display: 'flex', flexDirection: 'row',
        ...(activeRetention
          ? {
              outline: (t) => `2px solid ${t.palette.error.main}`,
              outlineOffset: '-2px',
              boxShadow: (t) => `0 0 8px ${t.palette.error.main}66`,
            }
          : anyStreamRecording
            ? {
                outline: (t) => `2px solid ${t.palette.error.main}`,
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
          {onToggleGridSolo != null && (
            <Tooltip title={gridSoloActive ? t('cameraCard.showAll') : t('cameraCard.expand')} placement="right">
              <IconButton size="small" onClick={onToggleGridSolo} sx={BTN_SX}>
                {gridSoloActive
                  ? <FullscreenExitIcon sx={{ fontSize: '0.95rem' }} />
                  : <FullscreenIcon sx={{ fontSize: '0.95rem' }} />}
              </IconButton>
            </Tooltip>
          )}

          <Tooltip title={mode === 'live' ? t('cameraCard.switchToArchive') : t('cameraCard.switchToLive')} placement="right">
            <IconButton size="small" onClick={() => setMode(mode === 'live' ? 'archive' : 'live')} sx={BTN_SX}>
              {mode === 'live'
                ? <LiveTvIcon sx={{ fontSize: '0.95rem' }} />
                : <VideoLibraryIcon sx={{ fontSize: '0.95rem' }} />}
            </IconButton>
          </Tooltip>

          {webRtcStreams.length > 1 && (
            <Tooltip title={t('cameraCard.cycleStream', { stream: activeStream })} placement="right">
              <IconButton size="small" onClick={cycleStream} sx={BTN_SX}>
                <LayersIcon sx={{ fontSize: '0.95rem' }} />
              </IconButton>
            </Tooltip>
          )}

          {camera.onvifConn && mode === 'live' && !isDisabled && (
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

          {isOnvifManaged && (
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
          )}

          {activeStreamObj && (
            <Tooltip
              title={activeStreamContinuous ? t('cameraCard.continuousOn') : t('cameraCard.continuousOff')}
              placement="right"
            >
              <span>
                <IconButton
                  size="small"
                  disabled={pendingContinuous || isDisabled}
                  onClick={toggleContinuousRecording}
                  sx={{
                    ...BTN_SX,
                    ...(activeStreamContinuous ? { color: 'error.main' } : {}),
                    ...((pendingContinuous || isDisabled) ? { opacity: 0.35 } : {}),
                  }}
                >
                  <FiberManualRecordIcon sx={{ fontSize: '0.95rem' }} />
                </IconButton>
              </span>
            </Tooltip>
          )}

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
            {camera.onvifConn && mode === 'live' && !isDisabled && (
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
            {rtsp && (
              <Tooltip title={copied ? t('cameraCard.copiedRtsp') : t('cameraCard.copyRtsp')}>
                <IconButton
                  size="small"
                  sx={{ p: '2px', color: copied ? '#4caf50' : alpha(W, 0.5), '&:hover': { color: W } }}
                  onClick={() => {
                    void navigator.clipboard.writeText(rtsp).then(() => {
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
                  {webRtcStreams.map((s) => (
                    <Tooltip key={s.streamName} title={isStreamRecording(s) ? t('cameraCard.streamRecordingTip') : ''}>
                      <Chip
                        label={s.streamName}
                        size="small"
                        icon={isStreamRecording(s) ? <FiberManualRecordIcon /> : undefined}
                        onClick={() => setSelectedStream(s.streamName)}
                        variant={activeStream === s.streamName ? 'filled' : 'outlined'}
                        sx={{
                          height: 20, fontSize: '0.68rem', cursor: 'pointer',
                          color: activeStream === s.streamName ? B : alpha(W, 0.75),
                          bgcolor: activeStream === s.streamName ? alpha(W, 0.9) : 'transparent',
                          borderColor: isStreamRecording(s) ? 'error.main' : alpha(W, 0.3),
                          '& .MuiChip-label': { px: 0.75 },
                          '& .MuiChip-icon': { fontSize: '0.55rem', color: 'error.main', ml: 0.6 },
                        }}
                      />
                    </Tooltip>
                  ))}
                </Stack>
              </Box>
            )}

            {/* Mode */}
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

            {/* Recording profile (base) */}
            {mode === 'live' && activeStreamObj && (
              <Box>
                <Typography component="span" sx={CAPTION_SX}>
                  {webRtcStreams.length > 1
                    ? t('cameraCard.recordingSectionForStream', { stream: activeStream })
                    : t('cameraCard.recordingSection')}
                </Typography>
                <Stack direction="row" alignItems="center" spacing={1} sx={{ px: 0.25 }}>
                  <StreamProfileRow cameraId={camera.cameraId} stream={activeStreamObj} />
                </Stack>
              </Box>
            )}

            {/* Override (automation) */}
            {mode === 'live' && (
              <Box>
                <Typography component="span" sx={CAPTION_SX}>
                  {webRtcStreams.length > 1
                    ? t('cameraCard.overrideSectionForStream', { stream: activeStream })
                    : t('cameraCard.overrideSection')}
                </Typography>
                <StreamOverrideRow cameraId={camera.cameraId} stream={activeStreamObj} />
              </Box>
            )}

            {/* Guard */}
            {isOnvifManaged && (
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
            )}

            {/* Solo */}
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
                      void navigator.clipboard.writeText(rtsp).then(() => {
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
      <Box sx={{ flex: 1, minWidth: 0, minHeight: 0, display: 'flex', overflow: 'hidden', position: 'relative' }}>
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
        {mode === 'live' && (
          <CameraStream
            streamName={streamName}
            baseUrl={`${window.location.protocol}//${merged.mediaMtxWebrtcHost || window.location.hostname}:${merged.mediaMtxWebrtcPort ?? 8889}`}
          />
        )}
        {mode === 'archive' && (
          <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
            <ArchiveHlsStreamCard cameraId={camera.cameraId} />
          </Box>
        )}
        {ptzOpen && mode === 'live' && (
          <PtzOverlay
            cameraId={camera.cameraId}
            profileToken={activeStreamObj?.profileToken ?? ''}
            onClose={() => setPtzOpen(false)}
          />
        )}
      </Box>
    </Card>
  )
}
