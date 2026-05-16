import { useEffect, useState } from 'react'
import { patchSlot, selectSlotUi } from '../store/monitorUiSlice'
import {
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
import { type CameraSetupDto } from '../dto/dtos'
import CameraStream from './CameraStream'
import ArchiveHlsStreamCard from './ArchiveHlsStreamCard'
import StreamOverrideRow from './StreamOverrideRow'
import PtzOverlay from './PtzOverlay'
import { useAppDispatch, useAppSelector } from '../store'
import { fetchCameraState } from '../store/liveSlice'

const STRIP_W = 28
const PANEL_W = 182

const STRIP_BG = { bgcolor: 'rgba(0,0,0,0.6)', backdropFilter: 'blur(6px)' } as const

const BTN_SX = {
  p: '3px',
  color: 'rgba(255,255,255,0.8)',
  borderRadius: '4px',
  '&:hover': { color: '#fff', bgcolor: 'rgba(255,255,255,0.12)' },
} as const

const CAPTION_SX = {
  display: 'block',
  fontSize: '0.6rem',
  color: 'rgba(255,255,255,0.45)',
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

  const [onvifRtsp, setOnvifRtsp] = useState('')

  const merged: CameraSetupDto = { ...camera, ...(cameraState ?? {}) }
  const recordingMode = merged.recordingMode ?? 'off'
  const webRtcStreams = (merged.streams ?? []).filter((s) => s.webRtc)
  const activeStream = selectedStream || webRtcStreams[0]?.streamName || ''
  const activeStreamObj =
    (merged.streams ?? []).find((s) => s.streamName === activeStream) ??
    (merged.streams ?? [])[0] ??
    null
  const activeRetention = merged.activeRetentionWindow === true && recordingMode !== 'off'

  const configuredRtsp = camera.rtspUrl || ''
  useEffect(() => {
    if (configuredRtsp || !camera.onvifConn) { setOnvifRtsp(''); return }
    const token = activeStreamObj?.profileToken ?? ''
    const qs = token ? `?profileToken=${encodeURIComponent(token)}` : ''
    fetch(`/api/onvif/cameras/${encodeURIComponent(camera.cameraId)}/stream-uri${qs}`)
      .then((r) => (r.ok ? (r.json() as Promise<{ rtspUrl: string }>) : Promise.reject()))
      .then((d) => setOnvifRtsp(d.rtspUrl || ''))
      .catch(() => setOnvifRtsp(''))
  }, [camera.cameraId, camera.onvifConn, configuredRtsp, activeStreamObj?.profileToken])

  const rtsp = configuredRtsp || onvifRtsp
  const rtspIsOnvif = !configuredRtsp && !!onvifRtsp

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
        bgcolor: 'black',
        ...(activeRetention
          ? {
              outline: (t) => `2px solid ${t.palette.error.main}`,
              outlineOffset: '-2px',
              boxShadow: (t) => `0 0 8px ${t.palette.error.main}66`,
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
            <Tooltip title={gridSoloActive ? 'Show all' : 'Expand'} placement="right">
              <IconButton size="small" onClick={onToggleGridSolo} sx={BTN_SX}>
                {gridSoloActive
                  ? <FullscreenExitIcon sx={{ fontSize: '0.95rem' }} />
                  : <FullscreenIcon sx={{ fontSize: '0.95rem' }} />}
              </IconButton>
            </Tooltip>
          )}

          <Tooltip title={mode === 'live' ? 'Switch to Archive' : 'Switch to Live'} placement="right">
            <IconButton size="small" onClick={() => setMode(mode === 'live' ? 'archive' : 'live')} sx={BTN_SX}>
              {mode === 'live'
                ? <LiveTvIcon sx={{ fontSize: '0.95rem' }} />
                : <VideoLibraryIcon sx={{ fontSize: '0.95rem' }} />}
            </IconButton>
          </Tooltip>

          {webRtcStreams.length > 1 && (
            <Tooltip title={`Stream: ${activeStream} — click to cycle`} placement="right">
              <IconButton size="small" onClick={cycleStream} sx={BTN_SX}>
                <LayersIcon sx={{ fontSize: '0.95rem' }} />
              </IconButton>
            </Tooltip>
          )}

          {camera.onvifConn && mode === 'live' && (
            <Tooltip title={ptzOpen ? 'Close PTZ' : 'PTZ control'} placement="right">
              <IconButton
                size="small"
                onClick={() => setPtzOpen(!ptzOpen)}
                sx={{ ...BTN_SX, ...(ptzOpen ? { color: '#fff', bgcolor: 'rgba(255,255,255,0.2)' } : {}) }}
              >
                <ControlCameraIcon sx={{ fontSize: '0.95rem' }} />
              </IconButton>
            </Tooltip>
          )}

          {activeRetention && (
            <Tooltip title="Retention episode active" placement="right">
              <FiberManualRecordIcon sx={{ fontSize: '0.6rem', color: 'error.main', my: 0.25 }} />
            </Tooltip>
          )}

          <Box sx={{ flex: 1 }} />

          <Tooltip title="Controls" placement="right">
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
            <Tooltip
              title={rtsp ? (rtspIsOnvif ? `ONVIF: ${rtsp}` : rtsp) : 'No RTSP'}
              placement="right"
            >
              <Typography
                variant="body2"
                sx={{ flex: 1, color: '#fff', fontWeight: 600, fontSize: '0.8rem', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', cursor: 'default' }}
              >
                {camera.cameraId}
              </Typography>
            </Tooltip>
            <IconButton
              size="small"
              onClick={() => setPanelOpen(false)}
              sx={{ p: '2px', color: 'rgba(255,255,255,0.5)', '&:hover': { color: '#fff' } }}
            >
              <CloseIcon sx={{ fontSize: '0.9rem' }} />
            </IconButton>
          </Stack>

          <Divider sx={{ borderColor: 'rgba(255,255,255,0.12)', flexShrink: 0 }} />

          <Stack spacing={1.25} sx={{ p: 1 }}>

            {/* Streams */}
            {webRtcStreams.length > 1 && (
              <Box>
                <Typography component="span" sx={CAPTION_SX}>Stream</Typography>
                <Stack direction="row" flexWrap="wrap" gap={0.5}>
                  {webRtcStreams.map((s) => (
                    <Chip
                      key={s.streamName}
                      label={s.streamName}
                      size="small"
                      onClick={() => setSelectedStream(s.streamName)}
                      variant={activeStream === s.streamName ? 'filled' : 'outlined'}
                      sx={{
                        height: 20, fontSize: '0.68rem', cursor: 'pointer',
                        color: activeStream === s.streamName ? '#000' : 'rgba(255,255,255,0.75)',
                        bgcolor: activeStream === s.streamName ? 'rgba(255,255,255,0.9)' : 'transparent',
                        borderColor: 'rgba(255,255,255,0.3)',
                        '& .MuiChip-label': { px: 0.75 },
                      }}
                    />
                  ))}
                </Stack>
              </Box>
            )}

            {/* Mode */}
            <Box>
              <Typography component="span" sx={CAPTION_SX}>Mode</Typography>
              <ToggleButtonGroup
                value={mode} exclusive onChange={(_, v) => v && setMode(v)} size="small" fullWidth
                sx={{
                  '& .MuiToggleButton-root': {
                    flex: 1, py: 0.25, fontSize: '0.7rem',
                    color: 'rgba(255,255,255,0.6)', borderColor: 'rgba(255,255,255,0.2)',
                    '&.Mui-selected': { color: '#fff', bgcolor: 'rgba(255,255,255,0.15)', borderColor: 'rgba(255,255,255,0.35)' },
                  },
                }}
              >
                <ToggleButton value="live">Live</ToggleButton>
                <ToggleButton value="archive">Archive</ToggleButton>
              </ToggleButtonGroup>
            </Box>

            {/* Override */}
            {mode === 'live' && (
              <Box>
                <Typography component="span" sx={CAPTION_SX}>Override</Typography>
                <StreamOverrideRow cameraId={camera.cameraId} stream={activeStreamObj} />
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
                  color: 'rgba(255,255,255,0.7)', borderColor: 'rgba(255,255,255,0.2)',
                  '&:hover': { color: '#fff', borderColor: 'rgba(255,255,255,0.5)', bgcolor: 'rgba(255,255,255,0.07)' },
                }}
              >
                {gridSoloActive ? 'Show all' : 'Expand'}
              </Button>
            )}

            {/* RTSP copy */}
            {rtsp && (
              <Stack direction="row" alignItems="center" gap={0.5}>
                <Typography sx={{ flex: 1, fontSize: '0.62rem', color: 'rgba(255,255,255,0.35)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {rtsp}
                </Typography>
                <Tooltip title="Copy RTSP">
                  <IconButton
                    size="small"
                    sx={{ p: '2px', color: 'rgba(255,255,255,0.45)', flexShrink: 0, '&:hover': { color: '#fff' } }}
                    onClick={() => navigator.clipboard.writeText(rtsp)}
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
                <Typography sx={{ fontSize: '0.65rem', color: 'error.light' }}>Retention active</Typography>
              </Stack>
            )}
          </Stack>
        </Stack>
      )}

      {/* ── Right: video / archive ── fills remaining space */}
      <Box sx={{ flex: 1, minWidth: 0, display: 'flex', overflow: 'hidden', position: 'relative' }}>
        {mode === 'live' && (
          <CameraStream streamName={streamName} baseUrl={merged.mediaMtxWebrtcUrl} />
        )}
        {mode === 'archive' && (
          <Box sx={{ flex: 1, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
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
