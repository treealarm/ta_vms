import { Box, Card, CardContent, IconButton, Slider, SpeedDial, SpeedDialAction, Stack, Tooltip, Typography } from '@mui/material'
import PlayArrowIcon from '@mui/icons-material/PlayArrow';
import PauseIcon from '@mui/icons-material/Pause';
import StopIcon from '@mui/icons-material/Stop';
import AccessTimeIcon from '@mui/icons-material/AccessTime';
import SkipPreviousIcon from '@mui/icons-material/SkipPrevious';
import SkipNextIcon from '@mui/icons-material/SkipNext';
import SpeedIcon from '@mui/icons-material/Speed';

import { useState, useEffect } from 'react'
import { type PlayerProgressStateDto, type RecordingDto } from '../dto/dtos'
import CameraStream from './CameraStream'
import RecordingsList from './RecordingsList'
import { fetchPlayerState, sendPlayerCommand, setPlayerState } from '../store/archSlice'
import { useAppDispatch, useAppSelector } from '../store'

type PlayerStreamCardProps = {
  player: PlayerProgressStateDto
  onRemove: (player: PlayerProgressStateDto) => Promise<void>
}

function PlayerStreamCard({ player }: PlayerStreamCardProps) {
  const dispatch = useAppDispatch()
  const [listOpen, setListOpen] = useState(false)
  const [speedDialOpen, setSpeedDialOpen] = useState(false)
  const [playbackSpeed, setPlaybackSpeed] = useState(1)
  const speedOptions = [0.25, 0.5, 1, 1.5, 2]

  const currentPlayer = useAppSelector(
    (state) => state.arch.playerStatesByClientId[player.client_id]
  ) ?? player

  useEffect(() => {
    const interval = setInterval(() => {
      void dispatch(fetchPlayerState(player.client_id))
    }, 1000)

    void dispatch(fetchPlayerState(player.client_id))
    return () => clearInterval(interval)
  }, [dispatch, player.client_id])

  const sendSeek = async (timeToSend: string, position: number) => {
    await dispatch(
      setPlayerState({
        camera_id: currentPlayer.camera_id,
        client_id: currentPlayer.client_id,
        position_ts: timeToSend,
        position_ms: position
      })
    )
  }

  const handleSliderCommitted = (_: Event | React.SyntheticEvent, value: number | number[]) => {
    if (typeof value === 'number' && currentPlayer.start_ts != null) {
      const startTime = new Date(currentPlayer.start_ts).getTime()
      const targetTs = new Date(startTime).toISOString()
      void sendSeek(targetTs, value)
    }
  }

  const handleSelectRecording = (rec: RecordingDto) => {
    void sendSeek(new Date(rec.startTime).toISOString(), 0)
  }

  const mode = currentPlayer.mode ?? 'unspecified'
  const isPlayActive = mode === 'playing'
  const isPauseActive = mode === 'paused'
  const isStopActive = mode === 'stopped'

  const handleSendCommand = (command: 'play' | 'pause' | 'stop' | 'step_back' | 'step_forward') => {
    void dispatch(
      sendPlayerCommand({
        client_id: currentPlayer.client_id,
        command,
        ...(command === 'play' ? { speed: playbackSpeed } : {})
      })
    )
  }

  const handlePlaybackSpeedSelect = (speed: number) => {
    setPlaybackSpeed(speed)
    setSpeedDialOpen(false)
  }

  return (
    <Card sx={{ height: '100%' }}>
      <CardContent>
        <Stack spacing={1.5} sx={{ height: '100%' }}>
          {/* Header */}
          {/* <Stack direction="row" justifyContent="space-between" alignItems="center">
            <Typography variant="h6">{player.camera_id}</Typography>
            <!IconButton size="small" onClick={() => void onRemove(player)}>✕</>
          </Stack>*/}

          {/* Video and recordings */}
          <Box sx={{ width: '100%', aspectRatio: '1 / 1', display: 'flex', minHeight: 0 }}>
            <Stack direction="row" spacing={1} sx={{ width: '100%', minHeight: 0 }}>
              <Box sx={{ flex: 1, minWidth: 0, minHeight: 0 }}>
                <CameraStream streamName={player.client_id} />
              </Box>

              {listOpen && (
                <Box sx={{
                  display: 'flex',
                  flexDirection: 'column',
                  borderLeft: '1px solid rgba(0,0,0,0.1)',
                  overflow: 'hidden',
                  minHeight: 0
                }}>
                  <RecordingsList
                    open={listOpen}
                    cameraId={player.camera_id}
                    timestamp={currentPlayer.position_ts}
                    filePath={currentPlayer.file_path}
                    onSelect={handleSelectRecording}
                  />
                </Box>
              )}
            </Stack>
          </Box>

          {/* Slider */}
          <Stack direction="row" spacing={1}>
            <Slider
              value={currentPlayer.position_ms}
              max={currentPlayer.duration_ms}
              onChangeCommitted={handleSliderCommitted}
            />
          </Stack>

          <Stack direction="row" spacing={1} justifyContent="center">
            <Tooltip title="Playback speed">
              <Box sx={{ position: 'relative', width: 40, height: 40 }}>
                <SpeedDial
                  ariaLabel="Playback speed selector"
                  icon={
                    <Stack alignItems="center" spacing={0}>
                      <SpeedIcon fontSize="small" />
                      <Typography variant="caption" sx={{ lineHeight: 1 }}>
                        {playbackSpeed}x
                      </Typography>
                    </Stack>
                  }
                  direction="up"
                  FabProps={{ size: 'small' }}
                  open={speedDialOpen}
                  onOpen={() => setSpeedDialOpen(true)}
                  onClose={() => setSpeedDialOpen(false)}
                >
                  {speedOptions.map((speed) => (
                    <SpeedDialAction
                      key={speed}
                      icon={<Typography variant="caption">{speed}x</Typography>}
                      tooltipTitle={`${speed}x`}
                      onClick={() => handlePlaybackSpeedSelect(speed)}
                      FabProps={{
                        color: playbackSpeed === speed ? 'primary' : 'default',
                        size: 'small'
                      }}
                    />
                  ))}
                </SpeedDial>
              </Box>
            </Tooltip>

            <Tooltip title="Step back">
              <IconButton
                size="small"
                onClick={() => handleSendCommand('step_back')}
              >
                <SkipPreviousIcon />
              </IconButton>
            </Tooltip>

            <Tooltip title="Step forward">
              <IconButton
                size="small"
                onClick={() => handleSendCommand('step_forward')}
              >
                <SkipNextIcon />
              </IconButton>
            </Tooltip>

            <Tooltip title={isPlayActive ? 'Currently playing' : 'Play'}>
              <IconButton
                size="small"
                color={isPlayActive ? 'primary' : 'default'}
                onClick={() => handleSendCommand('play')}
              >
                <PlayArrowIcon />
              </IconButton>
            </Tooltip>

            <Tooltip title={isPauseActive ? 'Paused' : 'Pause'}>
              <IconButton
                size="small"
                color={isPauseActive ? 'primary' : 'default'}
                onClick={() => handleSendCommand('pause')}
              >
                <PauseIcon />
              </IconButton>
            </Tooltip>

            <Tooltip title={isStopActive ? 'Stopped' : 'Stop'}>
              <IconButton
                size="small"
                color={isStopActive ? 'secondary' : 'default'}
                onClick={() => handleSendCommand('stop')}
              >
                <StopIcon />
              </IconButton>
            </Tooltip>

            <Tooltip title={listOpen ? 'Hide recordings' : 'Show recordings'}>
              <IconButton size="small" onClick={() => setListOpen(v => !v)}>
                <AccessTimeIcon />
              </IconButton>
            </Tooltip>
          </Stack>
        </Stack>
      </CardContent>
    </Card>
  )
}

export default PlayerStreamCard
