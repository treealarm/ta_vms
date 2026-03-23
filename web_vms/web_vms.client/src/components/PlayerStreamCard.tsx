import { useState } from 'react'
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

  const sendSeek = async (iso?: string) => {
    const timeToSend =
      iso ?? (newTime ? new Date(newTime).toISOString() : null)

    console.log("sendSeek", timeToSend)
    if (!timeToSend) return

    await fetch('/api/vms/players/state', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        cameraId: player.cameraId,
        clientId: player.clientId,
        position: timeToSend
      })
    })
  }

  const handleSelectRecording = (rec: RecordingDto) => {
    const date = new Date(rec.startTime)

    setNewTime(toLocalDatetimeInputValue(date)) // локально
    //sendSeek(date.toISOString()) // UTC в API
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