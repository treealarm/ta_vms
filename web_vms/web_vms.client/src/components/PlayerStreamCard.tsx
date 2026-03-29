import { Box, Button, Card, CardContent, IconButton, Slider, Stack, Typography } from '@mui/material'
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

function PlayerStreamCard({ player, onRemove }: PlayerStreamCardProps) {
  const dispatch = useAppDispatch()
  const [listOpen, setListOpen] = useState(false)

  const currentPlayer = useAppSelector(
    (state) => state.arch.playerStatesByClientId[player.client_id]
  ) ?? player

  const [sliderValue, setSliderValue] = useState(currentPlayer.position_ms)

  // Подтягиваем позицию каждые 3 секунды
  useEffect(() => {
    const interval = setInterval(() => {
      void dispatch(fetchPlayerState(player.client_id))
    }, 3000)

    void dispatch(fetchPlayerState(player.client_id))
    return () => clearInterval(interval)
  }, [dispatch, player.client_id])

  // Обновляем слайдер при внешнем обновлении позиции
  useEffect(() => {
    setSliderValue(currentPlayer.position_ms)
  }, [currentPlayer.position_ms])

  // Отправка seek на сервер
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

  const handleSliderChange = (_: Event, value: number | number[]) => {
    if (typeof value === 'number') setSliderValue(value)
  }

  const handleSliderCommitted = (_: Event | React.SyntheticEvent, value: number | number[]) => {
    if (typeof value === 'number' && currentPlayer.start_ts != null) {
      const startTime = new Date(currentPlayer.start_ts).getTime() // абсолютное начало
      const targetTs = new Date(startTime).toISOString()   // прибавляем позицию в ms
      void sendSeek(targetTs, value)                                       // передаём ISO, как нужно
    }
  }

  const handleSelectRecording = (rec: RecordingDto) => {
    void sendSeek(new Date(rec.startTime).toISOString(), 0)
  }

  const handleSendCommand = (command: 'play' | 'pause' | 'stop') => {
    void dispatch(
      sendPlayerCommand({
        client_id: currentPlayer.client_id,
        command
      })
    )
  }

  return (
    <Card sx={{ height: '100%' }}>
      <CardContent>
        <Stack spacing={1.5} sx={{ height: '100%' }}>
          {/* Верхний заголовок */}
          <Stack direction="row" justifyContent="space-between" alignItems="center">
            <Typography variant="h6">{player.camera_id}</Typography>
            <IconButton size="small" onClick={() => void onRemove(player)}>✕</IconButton>
          </Stack>

          {/* Видео и список */}
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

          {/* Слайдер */}
          <Stack direction="row" spacing={1}>
            <IconButton size="small" onClick={() => setListOpen(v => !v)}>🕘</IconButton>
            <Slider
              value={sliderValue}
              max={currentPlayer.duration_ms}
              onChange={handleSliderChange}
              onChangeCommitted={handleSliderCommitted}
            />
          </Stack>

          <Stack direction="row" spacing={1} justifyContent="center">
            <Button size="small" variant="outlined" onClick={() => handleSendCommand('play')}>Play</Button>
            <Button size="small" variant="outlined" onClick={() => handleSendCommand('pause')}>Pause</Button>
            <Button size="small" variant="outlined" color="error" onClick={() => handleSendCommand('stop')}>Stop</Button>
          </Stack>
        </Stack>
      </CardContent>
    </Card>
  )
}

export default PlayerStreamCard