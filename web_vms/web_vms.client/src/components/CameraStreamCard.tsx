import { useMemo, useState, useEffect } from 'react'
import {
  Box,
  Button,
  Card,
  CardContent,
  Stack, Typography, IconButton, TextField

} from '@mui/material'
import ContentCopyIcon from '@mui/icons-material/ContentCopy';

type CameraInfo = {
  cameraId: string
  rtspUrl: string
}

type CameraStateDto = {
  cameraId: string
  recording: boolean
  webRtc: boolean
}

type CameraStreamCardProps = {
  camera: CameraInfo
  isBusy: boolean
  onActionComplete: (message: string) => Promise<void>
  onActionError: (message: string) => void
  onRemove: (camera: CameraInfo) => Promise<void>
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
  const [cameraState, setCameraState] = useState<CameraStateDto | null>(null)

  const busy = isBusy || isCameraBusy
  const isArchiveCamera = camera.rtspUrl.trim().length === 0
  const streamId = isArchiveCamera ? `${camera.cameraId}_arch` : camera.cameraId
  const streamUrl = useMemo(
    () => `${WEBRTC_BASE_URL.replace(/\/$/, '')}/${encodeURIComponent(streamId)}/`,
    [streamId]
  )

  // функция для запроса состояния камеры
  const fetchCameraState = async () => {
    try {
      const response = await fetch(`/api/vms/cameras/${encodeURIComponent(camera.cameraId)}/state`)
      if (!response.ok) return
      const data: CameraStateDto = await response.json()
      setCameraState(data)
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Unknown error'
      onActionError(`Failed to fetch camera state: ${message}`)
    }
  }

  // начальный запрос и периодический polling
  useEffect(() => {
    let mounted = true
    const interval = setInterval(() => {
      if (mounted) fetchCameraState()
    }, 5000)

    fetchCameraState() // сразу при монтировании

    return () => {
      mounted = false
      clearInterval(interval)
    }
  }, [camera.cameraId])

  // функция переключения записи
  const updateRecordingState = async (nextRecordingState: boolean) => {
    setIsCameraBusy(true)
    try {
      const response = await fetch(`/api/vms/cameras/state`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ cameraid: camera.cameraId, recording: nextRecordingState, webRtc: true })
      })

      if (!response.ok) {
        onActionError(`Error: ${await response.text()}`)
        return
      }

      const result = await response.json()
      await onActionComplete(`Result: ${result.success ? 'OK' : 'Fail'}: ${result.message}`)
      // обновляем локальный state после успешного запроса
      setCameraState((prev) => prev ? { ...prev, recording: nextRecordingState } : prev)
    } catch (err) {
      const message = err instanceof Error ? err.message : 'Unknown error'
      onActionError(`Error: ${message}`)
    } finally {
      setIsCameraBusy(false)
    }
  }

  return (
    <Card sx={{ height: '100%' }}>
      <CardContent sx={{ p: 1.5 }}>
        <Stack spacing={1}>
          {/* Заголовок + кнопка удаления */}

          <Stack direction="row" spacing={1} alignItems="center">
            {/* Camera ID */}
            <Typography variant="subtitle1" sx={{ flexShrink: 0 }}>
              {camera.cameraId}
            </Typography>

            {/* Read-only URL с возможностью копирования */}
            <TextField
              value={camera.rtspUrl || 'Archive player'}
              InputProps={{
                readOnly: true,
                endAdornment: (
                  <IconButton
                    size="small"
                    onClick={() => {
                      navigator.clipboard.writeText(camera.rtspUrl || '');
                    }}
                  >
                    <ContentCopyIcon fontSize="small" />
                  </IconButton>
                )
              }}
              variant="outlined"
              size="small"
              sx={{ flexGrow: 1 }}
            />

            {/* Кнопка удаления */}
            <IconButton
              size="small"
              disabled={busy}
              aria-label={`Remove ${camera.cameraId}`}
              onClick={() => void onRemove(camera)}
              sx={{ flexShrink: 0 }}
            >
              ✕
            </IconButton>
          </Stack>

          {/* Видео */}
          <Box
            component="iframe"
            src={streamUrl}
            title={`Stream ${streamId}`}
            sx={{
              width: '100%',
              aspectRatio: '1 / 1',
              border: 1,
              borderColor: 'divider',
              borderRadius: 1,
              bgcolor: 'background.default'
            }}
          />

          {/* Кнопка REC */}
          <Button
            size="small"
            fullWidth
            disabled={busy || isArchiveCamera || !cameraState}
            variant={cameraState?.recording ? 'contained' : 'outlined'}
            color={cameraState?.recording ? 'error' : 'primary'}
            onClick={() => void updateRecordingState(!cameraState?.recording)}
          >
            REC
          </Button>
        </Stack>
      </CardContent>
    </Card>
  )
}

export type { CameraInfo, CameraStateDto }
export default CameraStreamCard