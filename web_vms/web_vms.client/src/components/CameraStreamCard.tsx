import { useEffect, useMemo } from 'react'
import {
  Button,
  Card,
  CardContent,
  Stack,
  Typography,
  IconButton,
  TextField,
  InputAdornment
} from '@mui/material'
import ContentCopyIcon from '@mui/icons-material/ContentCopy'
import type { CameraStateDto } from '../dto/dtos'
import CameraStream from './CameraStream'
import { fetchCameraState, updateCameraRecording } from '../store/liveSlice'
import { useAppDispatch, useAppSelector } from '../store'

type CameraStreamCardProps = {
  camera: CameraStateDto
  isBusy: boolean
  onRemove: (camera: CameraStateDto) => Promise<void>
}

function CameraStreamCard({ camera, isBusy, onRemove }: CameraStreamCardProps) {
  const dispatch = useAppDispatch()
  const cameraState = useAppSelector((state) => state.live.cameraStatesById[camera.cameraId])
  const busy = useMemo(() => isBusy, [isBusy])

  useEffect(() => {
    const interval = setInterval(() => {
      void dispatch(fetchCameraState(camera.cameraId))
    }, 5000)

    void dispatch(fetchCameraState(camera.cameraId))

    return () => {
      clearInterval(interval)
    }
  }, [camera.cameraId, dispatch])

  const updateRecordingState = async (nextRecordingState: boolean) => {
    await dispatch(
      updateCameraRecording({
        cameraId: camera.cameraId,
        recording: nextRecordingState,
        webRtc: true
      })
    )

    await dispatch(fetchCameraState(camera.cameraId))
  }

  return (
    <Card sx={{ height: '100%' }}>
      <CardContent sx={{ p: 1.5 }}>
        <Stack spacing={1}>
          <Stack direction="row" spacing={1} alignItems="center">
            <Typography variant="subtitle1" sx={{ flexShrink: 0 }}>
              {camera.cameraId}
            </Typography>

            <TextField
              value={camera.rtspUrl}
              variant="outlined"
              size="small"
              slotProps={{
                input: {
                  readOnly: true,
                  endAdornment: (
                    <InputAdornment position="end">
                      <IconButton size="small" onClick={() => navigator.clipboard.writeText(camera.rtspUrl || '')}>
                        <ContentCopyIcon fontSize="small" />
                      </IconButton>
                    </InputAdornment>
                  )
                }
              }}
              sx={{ flexGrow: 1 }}
            />

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

          <CameraStream streamName={camera.cameraId} />

          <Button
            size="small"
            fullWidth
            disabled={busy || !cameraState}
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

export default CameraStreamCard
