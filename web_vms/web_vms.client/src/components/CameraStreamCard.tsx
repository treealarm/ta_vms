import { useEffect, useState } from 'react'
import {
  Box,
  Button,
  Card,
  CardContent,
  Stack,
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

type CameraStreamCardProps = {
  camera: CameraInfo
  isBusy: boolean
  onActionComplete: (message: string) => Promise<void>
  onActionError: (message: string) => void
}

const getErrorMessage = async (response: Response): Promise<string> => {
  const text = await response.text()
  return text || response.statusText
}

function CameraStreamCard({
  camera,
  isBusy,
  onActionComplete,
  onActionError
}: CameraStreamCardProps) {
  const [isJpegStreamEnabled, setIsJpegStreamEnabled] = useState(false)
  const [frameUrl, setFrameUrl] = useState<string | null>(null)
  const [isCameraBusy, setIsCameraBusy] = useState(false)

  const busy = isBusy || isCameraBusy

  useEffect(() => {
    if (!isJpegStreamEnabled) {
      setFrameUrl((previousUrl) => {
        if (previousUrl) {
          URL.revokeObjectURL(previousUrl)
        }

        return null
      })
      return
    }

    const wsProtocol = window.location.protocol === 'https:' ? 'wss' : 'ws'
    const ws = new WebSocket(
      `${wsProtocol}://${window.location.host}/api/vms/cameras/${encodeURIComponent(camera.cameraId)}/jpeg/ws`
    )
    ws.binaryType = 'blob'

    ws.onmessage = (event) => {
      const objectUrl = URL.createObjectURL(event.data as Blob)
      setFrameUrl((previousUrl) => {
        if (previousUrl) {
          URL.revokeObjectURL(previousUrl)
        }

        return objectUrl
      })
    }

    ws.onerror = () => onActionError(`Error: WebSocket stream error for ${camera.cameraId}`)

    return () => {
      ws.close()
      setFrameUrl((previousUrl) => {
        if (previousUrl) {
          URL.revokeObjectURL(previousUrl)
        }

        return null
      })
    }
  }, [camera.cameraId, isJpegStreamEnabled, onActionError])

  const updateState = async (nextRecordingState: boolean, nextJpegState: boolean) => {
    setIsCameraBusy(true)
    try {
      const response = await fetch(`/api/vms/cameras/${encodeURIComponent(camera.cameraId)}/state`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ recording: nextRecordingState, jpegStream: nextJpegState })
      })

      if (!response.ok) {
        onActionError(`Error: ${await getErrorMessage(response)}`)
        return
      }

      const result: ApiResponse = await response.json()
      if (result.success) {
        setIsJpegStreamEnabled(nextJpegState)
      }
      await onActionComplete(`${result.success ? 'OK' : 'Fail'}: ${result.message}`)
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Unknown error'
      onActionError(`Error: ${message}`)
    } finally {
      setIsCameraBusy(false)
    }
  }

  return (
    <Card sx={{ height: '100%' }}>
      <CardContent>
        <Stack spacing={1.5}>
          <Typography variant="h6">{camera.cameraId}</Typography>
          <Typography variant="body2" color="text.secondary" noWrap title={camera.rtspUrl}>
            {camera.rtspUrl}
          </Typography>

          <Box
            component="img"
            src={frameUrl ?? undefined}
            alt={`Stream ${camera.cameraId}`}
            sx={{
              width: '100%',
              aspectRatio: '1 / 1',
              objectFit: 'contain',
              border: 1,
              borderColor: 'divider',
              borderRadius: 2,
              bgcolor: 'background.default'
            }}
          />

          <Stack direction="row" spacing={1.5}>
            <Button
              fullWidth
              disabled={busy}
              variant={camera.isRecording ? 'contained' : 'outlined'}
              color={camera.isRecording ? 'error' : 'primary'}
              onClick={() => void updateState(!camera.isRecording, isJpegStreamEnabled)}
            >
              REC
            </Button>
            <Button
              fullWidth
              disabled={busy}
              variant={isJpegStreamEnabled ? 'contained' : 'outlined'}
              color={isJpegStreamEnabled ? 'secondary' : 'primary'}
              onClick={() => void updateState(camera.isRecording, !isJpegStreamEnabled)}
            >
              JPEG STREAM
            </Button>
          </Stack>
        </Stack>
      </CardContent>
    </Card>
  )
}

export type { CameraInfo }
export default CameraStreamCard
