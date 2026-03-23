import { useEffect, useState } from 'react'
import {
  Box,
  Button,
  Card,
  CardContent,
  IconButton,
  Stack,
  Typography,
  TextField
} from '@mui/material'
import {
  toLocalDatetimeInputValue,
  type PlayerStateDto,
  type RecordingDto
} from '../dto/dtos'
import CameraStream from './CameraStream'
import RecordingsList from './RecordingsList'

type PlayerStreamCardProps = {
  player: PlayerStateDto
  isBusy: boolean
  onRemove: (player: PlayerStateDto) => Promise<void>
}

function PlayerStreamCard({
  player,
  isBusy,
  onRemove
}: PlayerStreamCardProps) {
  const [newTime, setNewTime] = useState('')
  const [listOpen, setListOpen] = useState(false)
  const [currentFilePath, setCurrentFilePath] = useState(player.filePath)
  const [currentPlayer, setCurrentPlayer] = useState<PlayerStateDto>(player)

  // Периодический опрос состояния плейера
  useEffect(() => {
    // создаём интервал один раз при монтировании компонента
    const interval = setInterval(async () => {
      try {
        const uri = `/api/vms/players/state?clientId=${encodeURIComponent(player.clientId)}`;
        console.log('fetch players', uri); // проверка вызова
        const resp = await fetch(uri)
        if (!resp.ok) return

        const updated: PlayerStateDto = await resp.json()

        // обновляем filePath только если изменился
        setCurrentFilePath(prev => (updated.filePath !== prev ? updated.filePath : prev))

        // обновляем остальное состояние плеера
        setCurrentPlayer(updated)
      } catch (err) {
        console.error('Error fetching player state', err)
      }
    }, 3000) // каждые 3 секунды

    // очищаем интервал при размонтировании
    return () => clearInterval(interval)
  }, [player.clientId]) // зависимость только от player.clientId

  const sendSeek = async (iso?: string) => {
    const timeToSend =
      iso ?? (newTime ? new Date(newTime).toISOString() : null)
    if (!timeToSend) return

    await fetch('/api/vms/players/state', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        cameraId: currentPlayer.cameraId,
        clientId: currentPlayer.clientId,
        position: timeToSend,
        filePath: currentFilePath
      })
    })
  }

  const handleSelectRecording = (rec: RecordingDto) => {
    const date = new Date(rec.startTime)

    setCurrentFilePath(rec.filePath);
    setNewTime(toLocalDatetimeInputValue(date)) // локально
    sendSeek(date.toISOString()) // UTC в API
  }

  return (
    <Card sx={{ height: '100%' }}>
      <CardContent>
        <Stack spacing={1.5} sx={{ height: '100%' }}>

          {/* HEADER */}
          <Stack
            direction="row"
            justifyContent="space-between"
            alignItems="center"
          >
            <Typography variant="h6">{player.cameraId}</Typography>

            <IconButton
              size="small"
              disabled={isBusy}
              onClick={() => void onRemove(player)}
            >
              ✕
            </IconButton>
          </Stack>

          {/* STREAM + LIST */}
          <Box
            sx={{
              width: '100%',
              aspectRatio: '1 / 1', // 🔑 единственный источник высоты
              display: 'flex',
              minHeight: 0
            }}
          >
            <Stack
              direction="row"
              spacing={1}
              sx={{
                width: '100%',
                minHeight: 0
              }}
            >
              {/* VIDEO */}
              <Box
                sx={{
                  flex: 1,
                  minWidth: 0,
                  minHeight: 0
                }}
              >
                <CameraStream streamName={player.clientId} />
              </Box>

              {/* LIST */}
              {listOpen && (
                <Box
                  sx={{
                    display: 'flex',
                    flexDirection: 'column',
                    borderLeft: '1px solid rgba(0,0,0,0.1)',
                    overflow: 'hidden',
                    minHeight: 0 // 🔑 критично
                  }}
                >
                  <RecordingsList
                    open={listOpen}
                    onClose={() => setListOpen(false)}
                    cameraId={player.cameraId}
                    timestamp={
                      newTime
                        ? new Date(newTime).toISOString()
                        : undefined
                    }
                    filePath={currentFilePath}
                    onSelect={handleSelectRecording}
                  />
                </Box>
              )}
            </Stack>
          </Box>

          {/* PICKER */}
          <Stack direction="row" spacing={1}>
            <TextField
              type="datetime-local"
              value={newTime}
              onChange={(e) => setNewTime(e.target.value)}
              fullWidth
              inputProps={{ step: 1 }}
            />

            <IconButton
              size="small"
              onClick={() => setListOpen((v) => !v)}
            >
              🕘
            </IconButton>

            <Button variant="contained" onClick={() => sendSeek()}>
              Go
            </Button>
          </Stack>
        </Stack>
      </CardContent>
    </Card>
  )
}

export default PlayerStreamCard