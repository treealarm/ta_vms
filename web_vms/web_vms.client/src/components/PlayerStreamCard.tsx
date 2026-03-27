import { useEffect, useState } from 'react'
import { Box, Card, CardContent, IconButton, Stack, Typography } from '@mui/material'
import { type PlayerProgressStateDto, type RecordingDto } from '../dto/dtos'
import CameraStream from './CameraStream'
import RecordingsList from './RecordingsList'
import { fetchPlayerState, setPlayerState } from '../store/archSlice'
import { useAppDispatch, useAppSelector } from '../store'

type PlayerStreamCardProps = {
  player: PlayerProgressStateDto
  onRemove: (player: PlayerProgressStateDto) => Promise<void>
}

function PlayerStreamCard({ player, onRemove }: PlayerStreamCardProps) {
  const dispatch = useAppDispatch()

  const [listOpen, setListOpen] = useState(false)

  const currentPlayer = useAppSelector((state) => state.arch.playerStatesByClientId[player.client_id]) ?? player
  const currentFilePath = currentPlayer.file_path
  const currentPosition = currentPlayer.position_ts

  useEffect(() => {
    const interval = setInterval(() => {
      void dispatch(fetchPlayerState(player.client_id))
    }, 3000)

    void dispatch(fetchPlayerState(player.client_id))

    return () => clearInterval(interval)
  }, [dispatch, player.client_id])

  const sendSeek = async (timeToSend: string) => {
    await dispatch(
      setPlayerState({
        camera_id: currentPlayer.camera_id,
        client_id: currentPlayer.client_id,
        position_ts: timeToSend
      })
    )
  }

  const handleSelectRecording = (rec: RecordingDto) => {
    void sendSeek(new Date(rec.startTime).toISOString())
  }

  return (
    <Card sx={{ height: '100%' }}>
      <CardContent>
        <Stack spacing={1.5} sx={{ height: '100%' }}>
          <Stack direction="row" justifyContent="space-between" alignItems="center">
            <Typography variant="h6">{player.camera_id}</Typography>

            <IconButton size="small" onClick={() => void onRemove(player)}>
              ✕
            </IconButton>
          </Stack>

          <Box
            sx={{
              width: '100%',
              aspectRatio: '1 / 1',
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
              <Box
                sx={{
                  flex: 1,
                  minWidth: 0,
                  minHeight: 0
                }}
              >
                <CameraStream streamName={player.client_id} />
              </Box>

              {listOpen && (
                <Box
                  sx={{
                    display: 'flex',
                    flexDirection: 'column',
                    borderLeft: '1px solid rgba(0,0,0,0.1)',
                    overflow: 'hidden',
                    minHeight: 0
                  }}
                >
                  <RecordingsList
                    open={listOpen}
                    cameraId={player.camera_id}
                    timestamp={currentPosition}
                    filePath={currentFilePath}
                    onSelect={handleSelectRecording}
                  />
                </Box>
              )}
            </Stack>
          </Box>

          <Stack direction="row" spacing={1}>
            <IconButton size="small" onClick={() => setListOpen((v) => !v)}>
              🕘
            </IconButton>
          </Stack>
        </Stack>
      </CardContent>
    </Card>
  )
}

export default PlayerStreamCard
