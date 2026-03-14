import { useCallback, useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  FormControl,
  Grid,
  InputLabel,
  MenuItem,
  Select,
  Stack,
  TextField,
  Typography
} from '@mui/material'

type ApiResponse = {
  success: boolean
  message: string
}

type CameraInfo = {
  cameraId: string
  rtspUrl: string
  isRecording: boolean
}

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

  const selectedCamera = cameras.find((camera) => camera.cameraId === cameraId)
  const isRecording = selectedCamera?.isRecording ?? false

  const loadCameras = useCallback(async (preferredCameraId?: string) => {
    try {
      const response = await fetch('/api/vms/cameras')
      if (!response.ok) {
        throw new Error(await getErrorMessage(response))
      }

      const cameraList: CameraInfo[] = await response.json()
      setCameras(cameraList)

      if (preferredCameraId) {
        const preferredCamera = cameraList.find((camera) => camera.cameraId === preferredCameraId)
        if (preferredCamera) {
          setCameraId(preferredCamera.cameraId)
          setRtspUrl(preferredCamera.rtspUrl)
          return
        }
      }

      const selectedCamera = cameraList.find((camera) => camera.cameraId === cameraId)
      if (selectedCamera) {
        setRtspUrl(selectedCamera.rtspUrl)
      }
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Unknown error'
      setStatus(`Error loading cameras: ${message}`)
    }
  }, [cameraId])

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
      await loadCameras(cameraId)
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Unknown error'
      setStatus(`Error: ${message}`)
    } finally {
      setIsBusy(false)
    }
  }

  const onAddCamera = async (event: FormEvent) => {
    event.preventDefault()
    const newCameraId = cameraId
    const hasStartTime = startTime.trim().length > 0

    await runAction(() =>
      fetch('/api/vms/cameras', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          cameraId: newCameraId,
          rtspUrl: hasStartTime ? '' : rtspUrl,
          state: { recording: false, jpegStream: false },
          startTime: hasStartTime ? startTime.trim() : null
        })
      })
    )

    await loadCameras(newCameraId)
  }

  const onCameraSelect = (selectedCameraId: string) => {
    setCameraId(selectedCameraId)
    const selectedCamera = cameras.find((camera) => camera.cameraId === selectedCameraId)
    if (selectedCamera) {
      setRtspUrl(selectedCamera.rtspUrl)
    }
  }

  const onToggleRecording = () => {
    void runAction(() =>
      fetch(`/api/vms/cameras/${encodeURIComponent(cameraId)}/state`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ recording: !isRecording, jpegStream: false })
      })
    )
  }

  return (
    <Box sx={{ maxWidth: 1100, mx: 'auto', p: { xs: 2, md: 4 } }}>
      <Stack spacing={2.5}>
        <Typography variant="h4" component="h1" fontWeight={700}>
          VMS gRPC Test Console
        </Typography>

        <Card>
          <CardContent>
            <Box component="form" onSubmit={onAddCamera}>
              <Stack spacing={2}>
                <Typography variant="h6">Camera</Typography>
                <Grid container spacing={2}>
                  <Grid size={{ xs: 12, md: 8 }}>
                    <FormControl fullWidth>
                      <InputLabel id="camera-select-label">Added cameras</InputLabel>
                      <Select
                        labelId="camera-select-label"
                        label="Added cameras"
                        value={cameras.some((camera) => camera.cameraId === cameraId) ? cameraId : ''}
                        onChange={(event) => onCameraSelect(event.target.value)}
                      >
                        <MenuItem value="">Select added camera</MenuItem>
                        {cameras.map((camera) => (
                          <MenuItem key={camera.cameraId} value={camera.cameraId}>
                            {camera.cameraId} ({camera.isRecording ? 'recording' : 'idle'})
                          </MenuItem>
                        ))}
                      </Select>
                    </FormControl>
                  </Grid>
                  <Grid size={{ xs: 12, md: 4 }}>
                    <Button
                      type="button"
                      variant="outlined"
                      fullWidth
                      disabled={isBusy}
                      sx={{ height: '100%' }}
                      onClick={() => void loadCameras(cameraId)}
                    >
                      Refresh list
                    </Button>
                  </Grid>
                </Grid>

                <TextField
                  label="Camera ID"
                  value={cameraId}
                  onChange={(event) => setCameraId(event.target.value)}
                  required
                  fullWidth
                />
                <TextField
                  label="Playback start time (YYYY-MM-DDTHH:mm:ss)"
                  value={startTime}
                  onChange={(event) => setStartTime(event.target.value)}
                  helperText="If provided, camera is recreated and playback starts from nearest recorded file time."
                  fullWidth
                />
                <TextField
                  label="RTSP URL"
                  value={rtspUrl}
                  onChange={(event) => setRtspUrl(event.target.value)}
                  required={startTime.trim().length === 0}
                  disabled={startTime.trim().length > 0}
                  fullWidth
                />
                <Button disabled={isBusy} type="submit" variant="contained">
                  Add camera
                </Button>
              </Stack>
            </Box>
          </CardContent>
        </Card>

        <Card>
          <CardContent>
            <Stack spacing={2}>
              <Typography variant="h6">Commands</Typography>
              <Grid container spacing={1.5}>
                <Grid size={{ xs: 12, sm: 6, lg: 3 }}>
                  <Button
                    fullWidth
                    disabled={isBusy}
                    variant={isRecording ? 'contained' : 'outlined'}
                    color={isRecording ? 'error' : 'primary'}
                    onClick={onToggleRecording}
                  >
                    REC
                  </Button>
                </Grid>
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
