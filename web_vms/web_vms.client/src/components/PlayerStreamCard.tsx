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
import type { PlayerInfo } from '../dto/dtos'

type PlayerStreamCardProps = {
  player: PlayerInfo
  isBusy: boolean
  onRemove: (player: PlayerInfo) => Promise<void>
}

const WEBRTC_BASE_URL =
  (import.meta.env.VITE_WEBRTC_BASE_URL as string | undefined)?.trim() ||
  'http://localhost:8889'

function PlayerStreamCard({
  player,
  isBusy,
  onRemove
}: PlayerStreamCardProps) {

  const [openPicker, setOpenPicker] = useState(false)
  const [newTime, setNewTime] = useState('')

  const streamUrl = useMemo(
    () => `${WEBRTC_BASE_URL.replace(/\/$/, '')}/${encodeURIComponent(player.clientId)}/`,
    [player.clientId]
  )

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

    setOpenPicker(false)
  }

  return (
    <Card sx={{ height: '100%' }}>
      <CardContent>
        <Stack spacing={1.5}>

          <Stack direction="row" justifyContent="space-between" alignItems="center">
            <Typography variant="h6">{player.cameraId}</Typography>

            <Stack direction="row">
              {/* КНОПКА КАЛЕНДАРЯ */}
              <IconButton
                size="small"
                disabled={isBusy}
                onClick={() => setOpenPicker((v) => !v)}
              >
                📅
              </IconButton>

              <IconButton
                size="small"
                disabled={isBusy}
                onClick={() => void onRemove(player)}
              >
                ✕
              </IconButton>
            </Stack>
          </Stack>

          <Typography variant="body2" color="text.secondary">
            Archive from: {player.startTime}
          </Typography>

          <Box
            component="iframe"
            src={streamUrl}
            title={`Player ${player.clientId}`}
            sx={{
              width: '100%',
              aspectRatio: '1 / 1',
              border: 1,
              borderColor: 'divider',
              borderRadius: 2
            }}
          />

          {/* PICKER */}
          {openPicker && (
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
          )}

          {/* базовые кнопки */}
          <Stack direction="row" spacing={1}>
            <Button fullWidth variant="outlined" disabled>
              Pause
            </Button>
          </Stack>

        </Stack>
      </CardContent>
    </Card>
  )
}

export default PlayerStreamCard