import { useMemo, useState } from 'react'
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
import { toLocalDatetimeInputValue, type PlayerStateDto, type RecordingDto } from '../dto/dtos'
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


    const timeToSend = iso ?? (newTime ? new Date(newTime).toISOString() : null)
    console.log('sendSeek', timeToSend)
    if (!timeToSend) {

      return
    }

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

    setNewTime(toLocalDatetimeInputValue(date)) // ✅ локально

    sendSeek(date.toISOString()) // ✅ в API по-прежнему UTC
  }

  return (
    <Card sx={{ height: '100%', position: 'relative' }}>
      <CardContent>
        <Stack spacing={1.5}>

          <Stack direction="row" justifyContent="space-between" alignItems="center">
            <Typography variant="h6">{player.cameraId}</Typography>

            <Stack direction="row">
             
              <IconButton
                size="small"
                disabled={isBusy}
                onClick={() => void onRemove(player)}
              >
                ✕
              </IconButton>
              
            </Stack>
          </Stack>

          <CameraStream streamName={player.clientId} />

          {/* PICKER */}
          
            <Stack direction="row" spacing={1}>
              <TextField
                type="datetime-local"
                value={newTime}
                onChange={(e) => setNewTime(e.target.value)}
              fullWidth
              inputProps={{ step: 1 }}
            />
            <IconButton size="small" onClick={() => setListOpen(true)}>
              🕘
            </IconButton>
            <Button variant="contained" onClick={() => sendSeek()}>
                Go
              </Button>
            </Stack>

          <RecordingsList
            open={listOpen}
            onClose={() => setListOpen(false)}
            cameraId={player.cameraId}
            timestamp={newTime ? new Date(newTime).toISOString() : undefined}
            onSelect={handleSelectRecording}
          />
        </Stack>
      </CardContent>
    </Card>
  )
}

export default PlayerStreamCard