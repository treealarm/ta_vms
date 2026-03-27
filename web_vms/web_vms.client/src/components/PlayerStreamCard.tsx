import { useEffect, useState } from 'react'
import { Box, Card, CardContent, IconButton, Stack, Typography } from '@mui/material'
import { type PlayerStateDto, type RecordingDto } from '../dto/dtos'
import CameraStream from './CameraStream'
import RecordingsList from './RecordingsList'
import { fetchPlayerState, seekPlayer } from '../store/archSlice'
import { useAppDispatch, useAppSelector } from '../store'

type PlayerStreamCardProps = {
  player: PlayerStateDto
  onRemove: (player: PlayerStateDto) => Promise<void>
}

function PlayerStreamCard({ player, onRemove }: PlayerStreamCardProps) {
  const dispatch = useAppDispatch()

  const [listOpen, setListOpen] = useState(false)

  const currentPlayer = useAppSelector((state) => state.arch.playerStatesByClientId[player.clientId]) ?? player
  const currentFilePath = currentPlayer.filePath
  const currentPosition = currentPlayer.position

  useEffect(() => {
    const interval = setInterval(() => {
      void dispatch(fetchPlayerState(player.clientId))
    }, 3000)

    void dispatch(fetchPlayerState(player.clientId))

    return () => clearInterval(interval)
  }, [dispatch, player.clientId])

  const sendSeek = async (timeToSend: string) => {
    await dispatch(
      seekPlayer({
        cameraId: currentPlayer.cameraId,
        clientId: currentPlayer.clientId,
        position: timeToSend,
        filePath: currentFilePath
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
            <Typography variant="h6">{player.cameraId}</Typography>

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
                <CameraStream streamName={player.clientId} />
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
                    cameraId={player.cameraId}
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
