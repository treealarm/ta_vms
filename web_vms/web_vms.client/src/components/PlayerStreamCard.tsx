import { useMemo } from 'react'
import {
  Box,
  Button,
  Card,
  CardContent,
  IconButton,
  Stack,
  Typography
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

  const streamUrl = useMemo(
    () => `${WEBRTC_BASE_URL.replace(/\/$/, '')}/${encodeURIComponent(player.clientId)}/`,
    [player.clientId]
  )

  return (
    <Card sx={{ height: '100%' }}>
      <CardContent>
        <Stack spacing={1.5}>
          <Stack direction="row" justifyContent="space-between" alignItems="center">
            <Typography variant="h6">{player.cameraId}</Typography>
            <IconButton
              size="small"
              disabled={isBusy}
              onClick={() => void onRemove(player)}
            >
              ✕
            </IconButton>
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

          {/* задел под будущие кнопки */}
          <Stack direction="row" spacing={1}>
            <Button fullWidth variant="outlined" disabled>
              Pause
            </Button>
            <Button fullWidth variant="outlined" disabled>
              Seek
            </Button>
          </Stack>
        </Stack>
      </CardContent>
    </Card>
  )
}

export default PlayerStreamCard