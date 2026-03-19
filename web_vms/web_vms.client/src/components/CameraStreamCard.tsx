import { useMemo, useState } from 'react'
import {
  Box,
  Button,
  Card,
  CardContent,
  IconButton,
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
  onRemove: (camera: CameraInfo) => Promise<void>
}

const getErrorMessage = async (response: Response): Promise<string> => {
  const text = await response.text()
  return text || response.statusText
}

const WEBRTC_BASE_URL = (import.meta.env.VITE_WEBRTC_BASE_URL as string | undefined)?.trim() || 'http://localhost:8889'

function CameraStreamCard({
  camera,
  isBusy,
  onActionComplete,
  onActionError,
  onRemove
}: CameraStreamCardProps) {
  const [isCameraBusy, setIsCameraBusy] = useState(false)

  const busy = isBusy || isCameraBusy
  const isArchiveCamera = camera.rtspUrl.trim().length === 0
  const streamId = isArchiveCamera ? `${camera.cameraId}_arch` : camera.cameraId
  const streamUrl = useMemo(() => `${WEBRTC_BASE_URL.replace(/\/$/, '')}/${encodeURIComponent(streamId)}/`, [streamId])

  const updateState = async (nextRecordingState: boolean) => {
    setIsCameraBusy(true)
    try {
      const response = await fetch(`/api/vms/cameras/${encodeURIComponent(camera.cameraId)}/state`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ recording: nextRecordingState, webRtc: true })
      })

      if (!response.ok) {
        onActionError(`Error: ${await getErrorMessage(response)}`)
        return
      }

      const result: ApiResponse = await response.json()
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
          <Stack direction="row" justifyContent="space-between" alignItems="center">
            <Typography variant="h6">{camera.cameraId}</Typography>
            <IconButton
              size="small"
              disabled={busy}
              aria-label={`Remove ${camera.cameraId}`}
              onClick={() => void onRemove(camera)}
            >
              ✕
            </IconButton>
          </Stack>
          <Typography variant="body2" color="text.secondary" noWrap title={camera.rtspUrl}>
            {camera.rtspUrl || 'Archive player'}
          </Typography>

          <Box
            component="iframe"
            src={streamUrl}
            title={`Stream ${streamId}`}
            sx={{
              width: '100%',
              aspectRatio: '1 / 1',
              border: 1,
              borderColor: 'divider',
              borderRadius: 2,
              bgcolor: 'background.default'
            }}
          />

          <Button
            fullWidth
            disabled={busy || isArchiveCamera}
            variant={camera.isRecording ? 'contained' : 'outlined'}
            color={camera.isRecording ? 'error' : 'primary'}
            onClick={() => void updateState(!camera.isRecording)}
          >
            REC
          </Button>
        </Stack>
      </CardContent>
    </Card>
  )
}

export type { CameraInfo }
export default CameraStreamCard
