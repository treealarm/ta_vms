import { useEffect, useMemo, useState } from 'react'
import { Box, Button, Card, CardContent, IconButton, Stack, Typography, TextField } from '@mui/material'
import { toLocalDatetimeInputValue, type PlayerStateDto, type RecordingDto } from '../dto/dtos'
import CameraStream from './CameraStream'
import RecordingsList from './RecordingsList'
import { fetchPlayerState, seekPlayer } from '../store/archSlice'
import { useAppDispatch, useAppSelector } from '../store'

type PlayerStreamCardProps = {
  player: PlayerStateDto
  isBusy: boolean
  onRemove: (player: PlayerStateDto) => Promise<void>
}

function PlayerStreamCard({ player, isBusy, onRemove }: PlayerStreamCardProps) {
  const dispatch = useAppDispatch()
  const [newTime, setNewTime] = useState('')
  const [listOpen, setListOpen] = useState(false)

  const currentPlayer = useAppSelector((state) => state.arch.playerStatesByClientId[player.clientId]) ?? player
  const currentFilePath = currentPlayer.filePath
  const busy = useMemo(() => isBusy, [isBusy])

  useEffect(() => {
    const interval = setInterval(() => {
      void dispatch(fetchPlayerState(player.clientId))
    }, 3000)

    void dispatch(fetchPlayerState(player.clientId))

    return () => clearInterval(interval)
  }, [dispatch, player.clientId])

  const sendSeek = async (iso?: string) => {
    const timeToSend = iso ?? (newTime ? new Date(newTime).toISOString() : null)
    if (!timeToSend) {
      return
    }

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
    const date = new Date(rec.startTime)
    setNewTime(toLocalDatetimeInputValue(date))
    void sendSeek(date.toISOString())
  }

  return (
    <Card sx={{ height: '100%' }}>
      <CardContent>
        <Stack spacing={1.5} sx={{ height: '100%' }}>
          <Stack direction="row" justifyContent="space-between" alignItems="center">
            <Typography variant="h6">{player.cameraId}</Typography>

            <IconButton size="small" disabled={busy} onClick={() => void onRemove(player)}>
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
                    onClose={() => setListOpen(false)}
                    cameraId={player.cameraId}
                    timestamp={newTime ? new Date(newTime).toISOString() : undefined}
                    filePath={currentFilePath}
                    onSelect={handleSelectRecording}
                  />
                </Box>
              )}
            </Stack>
          </Box>

          <Stack direction="row" spacing={1}>
            <TextField
              type="datetime-local"
              value={newTime}
              onChange={(e) => setNewTime(e.target.value)}
              fullWidth
              inputProps={{ step: 1 }}
            />

            <IconButton size="small" onClick={() => setListOpen((v) => !v)}>
              🕘
            </IconButton>

            <Button variant="contained" onClick={() => void sendSeek()}>
              Go
            </Button>
          </Stack>
        </Stack>
      </CardContent>
    </Card>
  )
}

export default PlayerStreamCard
