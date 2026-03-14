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
import CameraStreamCard, { type CameraInfo } from './components/CameraStreamCard'

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
  const [rtspUrl, setRtspUrl] = useState('rtsp://127.0.0.1:8554/stream')
  const [startTime, setStartTime] = useState('')
  const [status, setStatus] = useState('Ready')
  const [isBusy, setIsBusy] = useState(false)
  const [cameras, setCameras] = useState<CameraInfo[]>([])

  const visibleCameras = useMemo(() => cameras.slice(0, MAX_CAMERAS), [cameras])
  const isLimitReached = cameras.length >= MAX_CAMERAS

  const loadCameras = useCallback(async () => {
    try {
      const response = await fetch('/api/vms/cameras')
      if (!response.ok) {
        throw new Error(await getErrorMessage(response))
      }

      const cameraList: CameraInfo[] = await response.json()
      setCameras(cameraList)
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Unknown error'
      setStatus(`Error loading cameras: ${message}`)
    }
  }, [])

  useEffect(() => {
    void loadCameras()
  }, [loadCameras])

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
          state: { recording: false, jpegStream: false },
          startTime: null
        })
      })
    )
  }

  const onAddPlayer = async () => {
    if (isLimitReached) {
      setStatus(`Error: Maximum ${MAX_CAMERAS} cameras supported in grid view`)
      return
    }

    const trimmedStartTime = startTime.trim()
    if (!trimmedStartTime) {
      setStatus('Error: Playback start time is required for add player')
      return
    }

    await runAction(() =>
      fetch('/api/vms/cameras', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          cameraId,
          rtspUrl: '',
          state: { recording: false, jpegStream: false },
          startTime: trimmedStartTime
        })
      })
    )
  }

  return (
    <Box sx={{ maxWidth: 1200, mx: 'auto', p: { xs: 2, md: 4 } }}>
      <Stack spacing={2.5}>
        <Typography variant="h4" component="h1" fontWeight={700}>
          VMS gRPC Test Console
        </Typography>

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
                />

                <Grid container spacing={1.5} alignItems="stretch">
                  <Grid size={{ xs: 12, md: 9 }}>
                    <TextField
                      label="Playback start time (YYYY-MM-DDTHH:mm:ss)"
                      value={startTime}
                      onChange={(event) => setStartTime(event.target.value)}
                      helperText="Creates camera player from archive start time"
                      fullWidth
                    />
                  </Grid>
                  <Grid size={{ xs: 12, md: 3 }}>
                    <Button
                      type="button"
                      variant="outlined"
                      disabled={isBusy || isLimitReached}
                      fullWidth
                      sx={{ height: '100%' }}
                      onClick={() => void onAddPlayer()}
                    >
                      Add player
                    </Button>
                  </Grid>
                </Grid>

                <Grid container spacing={1.5} alignItems="stretch">
                  <Grid size={{ xs: 12, md: 9 }}>
                    <TextField
                      label="RTSP URL"
                      value={rtspUrl}
                      onChange={(event) => setRtspUrl(event.target.value)}
                      required
                      fullWidth
                    />
                  </Grid>
                  <Grid size={{ xs: 12, md: 3 }}>
                    <Button
                      disabled={isBusy || isLimitReached}
                      type="submit"
                      variant="contained"
                      fullWidth
                      sx={{ height: '100%' }}
                    >
                      Add camera
                    </Button>
                  </Grid>
                </Grid>

                <Button type="button" variant="text" disabled={isBusy} onClick={() => void loadCameras()}>
                  Refresh list
                </Button>
              </Stack>
            </Box>
          </CardContent>
        </Card>

        <Card>
          <CardContent>
            <Stack spacing={2}>
              <Typography variant="h6">Streams grid (up to 4 cameras)</Typography>
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
                    />
                  </Grid>
                ))}
              </Grid>
              {visibleCameras.length === 0 ? (
                <Typography color="text.secondary">No cameras added yet.</Typography>
              ) : null}
            </Stack>
          </CardContent>
        </Card>

        <Alert severity={status.startsWith('Error') ? 'error' : 'info'}>{status}</Alert>
      </Stack>
    </Box>
  )
}

export default App
