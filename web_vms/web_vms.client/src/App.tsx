import { useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'
import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  Grid,
  Stack,
  TextField,
  Typography
} from '@mui/material'
import CameraStreamCard from './components/CameraStreamCard'
import PlayerStreamCard from './components/PlayerStreamCard'
import { type AddCameraRequestDto, type AddPlayerRequestDto } from './dto/dtos'
import { addCamera, loadCameras, removeCamera, setLiveStatus } from './store/liveSlice'
import { addPlayer, loadPlayers, removePlayer } from './store/archSlice'
import { useAppDispatch, useAppSelector } from './store'

const MAX_CAMERAS = 4

function App() {
  const dispatch = useAppDispatch()
  const [cameraId, setCameraId] = useState('cam1')
  const [rtspUrl, setRtspUrl] = useState('rtsp://admin:$Power321@192.168.1.150:554/channel=0_stream=0&onvif=0.sdp?real_stream')

  const cameras = useAppSelector((state) => state.live.cameras)
  const players = useAppSelector((state) => state.arch.players)
  const status = useAppSelector((state) => state.live.status !== 'Ready' ? state.live.status : state.arch.status !== 'Ready' ? state.arch.status : state.fs.status)

  const visibleCameras = useMemo(() => cameras.slice(0, MAX_CAMERAS), [cameras])
  const isLimitReached = cameras.length + players.length >= MAX_CAMERAS

  useEffect(() => {
    void dispatch(loadCameras())
    void dispatch(loadPlayers())
  }, [dispatch])

  const refreshLists = async () => {
    await Promise.all([dispatch(loadCameras()), dispatch(loadPlayers())])
  }

  const onAddCamera = async (event: FormEvent) => {
    event.preventDefault()
    if (isLimitReached) {
      dispatch(setLiveStatus(`Error: Maximum ${MAX_CAMERAS} cameras supported in grid view`))
      return
    }

    const payload: AddCameraRequestDto = {
      cameraId,
      rtspUrl,
      state: { cameraId, recording: false, webRtc: true }
    }

    const result = await dispatch(addCamera(payload))
    if (!addCamera.rejected.match(result)) {
      await refreshLists()
    }
  }

  const onAddPlayer = async () => {
    if (isLimitReached) {
      dispatch(setLiveStatus(`Error: Maximum ${MAX_CAMERAS} cameras supported in grid view`))
      return
    }

    const payload: AddPlayerRequestDto = {
      cameraId,
      clientId: `${cameraId}_arch`,
      position: new Date().toISOString()
    }

    const result = await dispatch(addPlayer(payload))
    if (!addPlayer.rejected.match(result)) {
      await refreshLists()
    }
  }

  return (
    <Box sx={{ maxWidth: 1200, mx: 'auto', p: { xs: 2, md: 4 } }}>
      <Stack spacing={2.5}>
        <Card>
          <CardContent>
            <Box component="form" onSubmit={onAddCamera}>
              <Stack spacing={2}>
                <Typography variant="h6">Camera setup</Typography>

                <TextField
                  label="Camera ID"
                  value={cameraId}
                  onChange={(event) => setCameraId(event.target.value)}
                  required
                  fullWidth
                  size="small"
                />

                <Stack direction="row" spacing={1.5} alignItems="center">
                  <TextField
                    label="RTSP URL"
                    value={rtspUrl}
                    onChange={(event) => setRtspUrl(event.target.value)}
                    required
                    fullWidth
                    size="small"
                  />

                  <Button
                    disabled={isLimitReached}
                    type="submit"
                    variant="outlined"
                    size="small"
                    sx={{ minWidth: 120 }}
                  >
                    Add
                  </Button>
                </Stack>

                <Stack direction="row" spacing={1.5}>
                  <Button variant="outlined" onClick={() => void refreshLists()} fullWidth>
                    Refresh list
                  </Button>

                  <Button
                    variant="outlined"
                    disabled={isLimitReached}
                    onClick={() => void onAddPlayer()}
                    fullWidth
                  >
                    Add player
                  </Button>
                </Stack>
              </Stack>
            </Box>
          </CardContent>
        </Card>

        <Card>
          <CardContent>
            <Stack spacing={2}>
              <Typography variant="h6">Live Cameras</Typography>
              <Grid container spacing={2}>
                {visibleCameras.map((camera) => (
                  <Grid key={camera.cameraId} size={{ xs: 12, sm: 6 }}>
                    <CameraStreamCard
                      camera={camera}
                      onRemove={async (cameraToDelete) => {
                        const result = await dispatch(
                          removeCamera({
                            cameraId: cameraToDelete.cameraId,
                            isArchive: cameraToDelete.rtspUrl.trim().length === 0
                          })
                        )

                        if (!removeCamera.rejected.match(result)) {
                          await refreshLists()
                        }
                      }}
                    />
                  </Grid>
                ))}
              </Grid>
              <Typography variant="h6">Arch Players</Typography>

              <Grid container spacing={2}>
                {players.map((player) => (
                  <Grid key={player.clientId} size={{ xs: 12, sm: 6 }}>
                    <PlayerStreamCard
                      player={player}
                      onRemove={async (playerToDelete) => {
                        const result = await dispatch(removePlayer(playerToDelete.clientId))

                        if (!removePlayer.rejected.match(result)) {
                          await refreshLists()
                        }
                      }}
                    />
                  </Grid>
                ))}
              </Grid>
            </Stack>
          </CardContent>
        </Card>

        <Alert severity={status.startsWith('Error') ? 'error' : 'info'}>{status}</Alert>
      </Stack>
    </Box>
  )
}

export default App
