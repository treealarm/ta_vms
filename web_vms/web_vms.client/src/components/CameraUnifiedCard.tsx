import { useEffect, useState } from 'react'
import {
  Box,
  Card,
  CardContent,
  IconButton,
  InputAdornment,
  Stack,
  TextField,
  ToggleButton,
  ToggleButtonGroup,
  Tooltip,
  Typography
} from '@mui/material'
import ContentCopyIcon from '@mui/icons-material/ContentCopy'
import FullscreenIcon from '@mui/icons-material/Fullscreen'
import FullscreenExitIcon from '@mui/icons-material/FullscreenExit'
import { type CameraSetupDto } from '../dto/dtos'
import CameraStreamCard from './CameraStreamCard'
import ArchiveHlsStreamCard from './ArchiveHlsStreamCard'
import StreamOverrideRow from './StreamOverrideRow'
import { useAppDispatch, useAppSelector } from '../store'
import { fetchCameraState } from '../store/liveSlice'

type CameraUnifiedCardProps = {
  camera: CameraSetupDto
  /** This camera fills the monitor grid; others are hidden. */
  gridSoloActive?: boolean
  /** Toggle between solo (this camera) and full quad grid. */
  onToggleGridSolo?: () => void
}

function CameraUnifiedCard({ camera, gridSoloActive = false, onToggleGridSolo }: CameraUnifiedCardProps) {
  const dispatch = useAppDispatch()

  const cameraState = useAppSelector((state) => state.live.cameraStatesById[camera.cameraId])

  const [mode, setMode] = useState<'live' | 'archive'>('live')
  const [selectedStreamName, setSelectedStreamName] = useState<string>('')

  const merged: CameraSetupDto = { ...camera, ...(cameraState ?? {}) }
  const recordingMode = merged.recordingMode ?? 'off'

  const webRtcStreams = (merged.streams ?? []).filter((s) => s.webRtc)
  const activeStreamName = selectedStreamName || webRtcStreams[0]?.streamName || ''
  const activeStream =
    (merged.streams ?? []).find((s) => s.streamName === activeStreamName)
    ?? (merged.streams ?? [])[0]
    ?? null

  const activeRetentionWindow =
    merged.activeRetentionWindow === true && recordingMode !== 'off'
  const retentionTooltip =
    'Retention episode active — segments finalized now are retained on disk (not subject to normal pruning)'

  const configuredRtsp = camera.rtspUrl || ''
  const [onvifRtsp, setOnvifRtsp] = useState('')

  useEffect(() => {
    if (configuredRtsp || !camera.onvifConn) {
      setOnvifRtsp('')
      return
    }
    const profileToken = activeStream?.profileToken ?? ''
    const qs = profileToken ? `?profileToken=${encodeURIComponent(profileToken)}` : ''
    fetch(`/api/onvif/cameras/${encodeURIComponent(camera.cameraId)}/stream-uri${qs}`)
      .then((r) => r.ok ? r.json() as Promise<{ rtspUrl: string }> : Promise.reject())
      .then((data) => setOnvifRtsp(data.rtspUrl || ''))
      .catch(() => setOnvifRtsp(''))
  }, [camera.cameraId, camera.onvifConn, configuredRtsp, activeStream?.profileToken])

  const displayRtsp = configuredRtsp || onvifRtsp
  const rtspIsOnvif = !configuredRtsp && !!onvifRtsp

  useEffect(() => {
    const interval = setInterval(() => {
      void dispatch(fetchCameraState(camera.cameraId))
    }, 5000)
    void dispatch(fetchCameraState(camera.cameraId))
    return () => {
      clearInterval(interval)
    }
  }, [camera.cameraId, dispatch])

  return (
    <Card
      sx={{
        height: '100%',
        width: '100%',
        minHeight: 0,
        display: 'flex',
        flexDirection: 'column',
        overflow: 'hidden',
        ...(activeRetentionWindow
          ? {
              outline: (theme) => `3px solid ${theme.palette.error.main}`,
              outlineOffset: '-3px',
              boxShadow: (theme) => `0 0 10px 1px ${theme.palette.error.main}55`
            }
          : {})
      }}
    >
      <CardContent
        sx={{
          flex: 1,
          minHeight: 0,
          display: 'flex',
          flexDirection: 'column',
          overflow: 'hidden',
          pt: 1,
          px: 1.25,
          pb: 1,
          '&:last-child': { pb: 1 }
        }}
      >
        <Stack spacing={0.75} sx={{ flex: 1, minHeight: 0, overflow: 'hidden' }}>
          <Stack direction="row" alignItems="center" sx={{ flexShrink: 0, flexWrap: 'nowrap', gap: 0.5 }}>
            {activeRetentionWindow && (
              <Tooltip title={retentionTooltip}>
                <Box
                  component="span"
                  sx={{
                    width: 8,
                    height: 8,
                    borderRadius: '50%',
                    bgcolor: 'error.main',
                    flexShrink: 0,
                    alignSelf: 'center'
                  }}
                />
              </Tooltip>
            )}
            <Typography variant="body2" sx={{ flexShrink: 0, fontWeight: 600, fontSize: '0.8125rem', lineHeight: 1.2 }}>
              {camera.cameraId}
            </Typography>

            <Tooltip title={rtspIsOnvif ? 'RTSP from ONVIF (not configured manually)' : ''} disableHoverListener={!rtspIsOnvif}>
              <TextField
                value={displayRtsp}
                variant="outlined"
                size="small"
                margin="none"
                hiddenLabel
                placeholder="RTSP"
                slotProps={{
                  input: {
                    readOnly: true,
                    sx: {
                      py: 0.5, px: 1, fontSize: '0.75rem',
                      ...(rtspIsOnvif ? { color: 'text.secondary', fontStyle: 'italic' } : {})
                    },
                    endAdornment: (
                      <InputAdornment position="end" sx={{ ml: 0 }}>
                        <IconButton
                          size="small"
                          edge="end"
                          sx={{ p: 0.25 }}
                          onClick={() => navigator.clipboard.writeText(displayRtsp)}
                        >
                          <ContentCopyIcon sx={{ fontSize: '1rem' }} />
                        </IconButton>
                      </InputAdornment>
                    )
                  }
                }}
                sx={{ flex: '1 1 80px', minWidth: 64 }}
              />
            </Tooltip>

            {mode === 'live' && <StreamOverrideRow cameraId={camera.cameraId} stream={activeStream} />}

            <ToggleButtonGroup
              value={mode}
              exclusive
              onChange={(_, v) => v && setMode(v)}
              size="small"
              sx={{
                flexShrink: 0,
                '& .MuiToggleButton-root': { py: 0.125, px: 0.75, fontSize: '0.75rem', lineHeight: 1.2 }
              }}
            >
              <ToggleButton value="live">Live</ToggleButton>
              <ToggleButton value="archive">Archive</ToggleButton>
            </ToggleButtonGroup>

            {onToggleGridSolo != null && (
              <Tooltip title={gridSoloActive ? 'Show all cameras' : 'Expand to fill grid'}>
                <IconButton
                  size="small"
                  onClick={onToggleGridSolo}
                  sx={{ p: 0.25, flexShrink: 0 }}
                  aria-label={gridSoloActive ? 'Show all cameras' : 'Expand camera in grid'}
                >
                  {gridSoloActive ? (
                    <FullscreenExitIcon sx={{ fontSize: '1.05rem' }} />
                  ) : (
                    <FullscreenIcon sx={{ fontSize: '1.05rem' }} />
                  )}
                </IconButton>
              </Tooltip>
            )}
          </Stack>

          <Box
            sx={{
              flex: 1,
              minHeight: 0,
              display: mode === 'live' ? 'flex' : 'none',
              flexDirection: 'column',
              overflow: 'hidden'
            }}
          >
            <CameraStreamCard camera={camera} onStreamChange={setSelectedStreamName} />
          </Box>

          {mode === 'archive' && (
            <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
              <ArchiveHlsStreamCard cameraId={camera.cameraId} />
            </Box>
          )}
        </Stack>
      </CardContent>
    </Card>
  )
}

export default CameraUnifiedCard
