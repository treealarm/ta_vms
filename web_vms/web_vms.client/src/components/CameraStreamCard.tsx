import { useEffect } from 'react'
import {
  Button,
  Card,
  CardContent,
  Stack,
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
  onRemove: (camera: CameraStateDto) => Promise<void>
}

function CameraStreamCard({ camera, onRemove }: CameraStreamCardProps) {
  const dispatch = useAppDispatch()
  const cameraState = useAppSelector((state) => state.live.cameraStatesById[camera.cameraId])

  useEffect(() => {
    const interval = setInterval(() => {
      dispatch(fetchCameraState(camera.cameraId))
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
