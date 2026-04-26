import { useEffect, useState } from 'react'
import {
  Box,
  Card,
  CardContent,
  Stack,
  ToggleButton,
  ToggleButtonGroup,
  Typography
} from '@mui/material'
import { getTabId, type CameraStateDto, type PlayerProgressStateDto, type PlayerStateDto } from '../dto/dtos'
import CameraStreamCard from './CameraStreamCard'
import PlayerStreamCard from './PlayerStreamCard'
import { useAppDispatch, useAppSelector } from '../store'
import { addPlayer, loadPlayers, sendPlayerCommand } from '../store/archSlice'

type CameraUnifiedCardProps = {
  camera: CameraStateDto
  onRemoveCamera: (camera: CameraStateDto) => Promise<void>
  onRemovePlayer: (player: PlayerProgressStateDto) => Promise<void>
}
function CameraUnifiedCard({ camera, onRemoveCamera, onRemovePlayer }: CameraUnifiedCardProps) {

  const dispatch = useAppDispatch()

  const players = useAppSelector((state) => state.arch.players)

  const player = players.find(p => p.camera_id === camera.cameraId)

  const [mode, setMode] = useState<'live' | 'archive'>('live')

  const createPlayer = async () => {

    const clientId =
      `${getTabId()}_`+
    `${camera.cameraId}_arch`;

    const payload: PlayerStateDto = {
      camera_id: camera.cameraId,
      client_id: clientId,
      position_ts: new Date().toISOString()
    }

    const result = await dispatch(addPlayer(payload))

    if (!addPlayer.rejected.match(result)) {
      await dispatch(loadPlayers())
    }
  }


  useEffect(() => {
    if (mode === 'archive' && !player) {
      void createPlayer()
      return
    }

    if (mode === 'live' && player) {
      void dispatch(
        sendPlayerCommand({
          camera_id: player.camera_id,
          client_id: player.client_id,
          command: 'stop' // или 'pause'
        })
      )
    }
  }, [mode, player, dispatch])

  return (
    <Card sx={{ height: '100%' }}>
      <CardContent>
        <Stack spacing={1.5}>

          <Stack direction="row" alignItems="center" justifyContent="space-between">
            <Typography variant="subtitle1" sx={{ flexShrink: 0 }}>
              {camera.cameraId}
            </Typography>

            <ToggleButtonGroup
              value={mode}
              exclusive
              onChange={(_, v) => v && setMode(v)}
              size="small"
            >
              <ToggleButton value="live">Live</ToggleButton>
              <ToggleButton value="archive">Archive</ToggleButton>
            </ToggleButtonGroup>
          </Stack>

          <Box sx={{ display: mode === 'live' ? 'block' : 'none' }}>
            <CameraStreamCard
              camera={camera}
              onRemove={onRemoveCamera}
            />
          </Box>

          {player && (
            <Box sx={{ display: mode === 'archive' ? 'block' : 'none' }}>
              <PlayerStreamCard
                player={player}
                onRemove={onRemovePlayer}
              />
            </Box>
          )}

        </Stack>
      </CardContent>
    </Card>
  )
}

export default CameraUnifiedCard

