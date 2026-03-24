import { useCallback, useEffect, useMemo, useState } from 'react'
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

import { type CameraStateDto, type PlayerStateDto } from './dto/dtos'

type ApiResponse = {
  success: boolean
  message: string
}


const MAX_CAMERAS = 4

const getErrorMessage = async (response: Response): Promise<string> => {
  const text = await response.text()
  return text || response.statusText
}

function App() {
  const [cameraId, setCameraId] = useState('cam1')
  const [rtspUrl, setRtspUrl] = useState('rtsp://orwell:elveesadm@172.16.80.109:554/axis-media/media.amp')
  const [status, setStatus] = useState('Ready')
  const [isBusy, setIsBusy] = useState(false)
  const [cameras, setCameras] = useState<CameraStateDto[]>([])
  const [players, setPlayers] = useState<PlayerStateDto[]>([])
  //rtsp://orwell:elvees@172.16.80.116/onvif-media/media.amp?profile=profile_1_h264&sessiontimeout=60&streamtype=unicast
  //rtsp://orwell:elvees@172.16.80.155/onvif-media/media.amp?profile=quality_h264&sessiontimeout=60&streamtype=unicast
  //rtsp://orwell:elvees@172.16.254.137/onvif-media/media.amp?profile=quality_h264&sessiontimeout=60&streamtype=unicast4
  //rtsp://orwell:elveesadm@172.16.80.109:554/axis-media/media.amp
  const visibleCameras = useMemo(() => cameras.slice(0, MAX_CAMERAS), [cameras])
  const isLimitReached = cameras.length + players.length >= MAX_CAMERAS

  const loadCameras = useCallback(async () => {
    try {
      const response = await fetch('/api/vms/cameras')
      if (!response.ok) {
        throw new Error(await getErrorMessage(response))
      }

      const cameraList: CameraStateDto[] = await response.json()
      setCameras(cameraList)
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Unknown error'
      setStatus(`Error loading cameras: ${message}`)
    }
  }, [])

  const loadPlayers = useCallback(async () => {
    try {
      const response = await fetch('/api/vms/players')
      if (!response.ok) {
        throw new Error(await getErrorMessage(response))
      }

      const list: PlayerStateDto[] = await response.json()
      setPlayers(list)
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Unknown error'
      setStatus(`Error loading players: ${message}`)
    }
  }, [])

  useEffect(() => {
    loadCameras();
    loadPlayers();
  }, [loadCameras, loadPlayers])

  const runAction = async (action: () => Promise<Response>) => {
    setIsBusy(true)
    try {
      const response = await action()
      if (!response.ok) {
        setStatus(`Error: ${await getErrorMessage(response)}`)
        return
      }

      const result: ApiResponse = await response.json()
      setStatus(`${result.success ? 'OK' : 'Fail'}: ${result.message}`)
      await loadCameras()
      await loadPlayers()
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Unknown error'
      setStatus(`Error: ${message}`)
    } finally {
      setIsBusy(false)
    }
  }

  const onAddCamera = async (event: FormEvent) => {
    event.preventDefault()
    if (isLimitReached) {
      setStatus(`Error: Maximum ${MAX_CAMERAS} cameras supported in grid view`)
      return
    }

    await runAction(() =>
      fetch('/api/vms/cameras', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          cameraId,
          rtspUrl,
          state: { cameraId: cameraId, recording: false, webRtc: true }
        })
      })
    )
  }

  const onAddPlayer = async () => {
    if (isLimitReached) {
      setStatus(`Error: Maximum ${MAX_CAMERAS} cameras supported in grid view`)
      return
    }

    const iso = new Date().toISOString();

    await runAction(() =>
      fetch('/api/vms/players', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          cameraId,
          clientId:cameraId+"_arch",
          position: iso
        })
      })
    )
  }


  const onRemoveCamera = async (camera: CameraStateDto) => {
    await runAction(() =>
      fetch(`/api/vms/cameras/${encodeURIComponent(camera.cameraId)}?isArchive=${camera.rtspUrl.trim().length === 0}`, {
        method: 'DELETE'
      })
    )
  }

  const onRemovePlayer = async (p: PlayerStateDto) => {
    await runAction(() =>
      fetch(`/api/vms/players/${encodeURIComponent(p.clientId)}`, {
        method: 'DELETE'
      })
    )
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
                    disabled={isBusy || isLimitReached}
                    type="submit"
                    variant="outlined"
                    size="small"
                    sx={{ minWidth: 120 }}
                  >
                    Add
                  </Button>
                </Stack>

                <Stack direction="row" spacing={1.5}>
                  <Button
                    variant="outlined"
                    disabled={isBusy}
                    onClick={() => void loadCameras()}
                    fullWidth
                  >
                    Refresh list
                  </Button>

                  <Button
                    variant="outlined"
                    disabled={isBusy || isLimitReached}
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
                      isBusy={isBusy}
                      onActionComplete={async (message) => {
                        setStatus(message)
                        await loadCameras()
                      }}
                      onActionError={setStatus}
                      onRemove={onRemoveCamera}
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
                      isBusy={isBusy}
                      onRemove={onRemovePlayer}
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
