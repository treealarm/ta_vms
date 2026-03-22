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
import type { PlayerStateDto } from '../dto/dtos'
import CameraStream from './CameraStream'

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

  const sendSeek = async () => {
    if (!newTime) return

    const iso = new Date(newTime).toISOString()

    await fetch('/api/vms/players/state', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        cameraId: player.cameraId,
        clientId: player.clientId,
        startTime: iso
      })
    })
  }

  return (
    <Card sx={{ height: '100%' }}>
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
              />
              <Button variant="contained" onClick={sendSeek}>
                Go
              </Button>
            </Stack>

        </Stack>
      </CardContent>
    </Card>
  )
}

export default PlayerStreamCard