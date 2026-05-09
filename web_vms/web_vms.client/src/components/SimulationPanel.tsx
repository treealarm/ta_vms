import { useState } from 'react'
import {
  Button,
  Card,
  CardContent,
  Divider,
  MenuItem,
  Stack,
  TextField,
  Typography
} from '@mui/material'

import type { CameraStateDto, RetentionEventDto } from '../dto/dtos'
import { useAppDispatch } from '../store'
import { sendRetentionEvent } from '../store/fsSlice'

type SimulationPanelProps = {
  cameras: CameraStateDto[]
}

export default function SimulationPanel({ cameras }: SimulationPanelProps) {
  const dispatch = useAppDispatch()
  const [cameraId, setCameraId] = useState('')
  const [eventId, setEventId] = useState('test-alarm')
  const [alarmStartTs, setAlarmStartTs] = useState('')
  const [timeoutSeconds, setTimeoutSeconds] = useState(30)

  const selectedCameraId = cameraId || cameras[0]?.cameraId || ''
  const selectedCamera = cameras.find((camera) => camera.cameraId === selectedCameraId)
  const preRecordSeconds = selectedCamera?.preAlarm?.preRecordSeconds ?? 15
  const postRecordSeconds = selectedCamera?.preAlarm?.postRecordSeconds ?? 15

  const setNonNegativeNumber = (raw: string, setter: (value: number) => void) => {
    const value = Number(raw)
    if (!Number.isNaN(value)) {
      setter(Math.max(0, value))
    }
  }

  const newEventId = () => {
    setEventId(`test-alarm-${selectedCameraId || 'camera'}-${Date.now()}`)
    setAlarmStartTs('')
  }

  const currentEventId = () => eventId.trim() || `test-alarm-${selectedCameraId}-${Date.now()}`

  const sendAlarmStart = async () => {
    if (!selectedCameraId) {
      return
    }

    const now = new Date().toISOString()
    const nextEventId = currentEventId()
    setEventId(nextEventId)
    setAlarmStartTs(now)

    const payload: RetentionEventDto = {
      eventId: nextEventId,
      cameraId: selectedCameraId,
      alarmStartTs: now,
      preRecordSeconds,
      postRecordSeconds,
      timeoutSeconds
    }

    await dispatch(sendRetentionEvent(payload))
  }

  const sendAlarmEnd = async () => {
    if (!selectedCameraId || !alarmStartTs) {
      return
    }

    const payload: RetentionEventDto = {
      eventId: currentEventId(),
      cameraId: selectedCameraId,
      alarmStartTs,
      alarmEndTs: new Date().toISOString(),
      preRecordSeconds,
      postRecordSeconds,
      timeoutSeconds
    }

    await dispatch(sendRetentionEvent(payload))
    setAlarmStartTs('')
  }

  return (
    <Card elevation={2} sx={{ maxWidth: 480 }}>
      <CardContent>
        <Stack spacing={2}>
          <Typography variant="h6">Test actions</Typography>

          <Divider />

          <Typography variant="subtitle2">Pre-alarm retention event</Typography>

          <TextField
            select
            label="Camera"
            value={selectedCameraId}
            onChange={(event) => setCameraId(event.target.value)}
            size="small"
            fullWidth
          >
            {cameras.map((camera) => (
              <MenuItem key={camera.cameraId} value={camera.cameraId}>
                {camera.cameraId}
              </MenuItem>
            ))}
          </TextField>

          <TextField
            label="Event ID"
            value={eventId}
            onChange={(event) => setEventId(event.target.value)}
            helperText="Start and end use the same event id"
            size="small"
            fullWidth
          />

          <Button variant="outlined" onClick={newEventId}>
            Generate event id
          </Button>

          <Typography variant="body2" color="text.secondary">
            Pre/post are camera settings: {preRecordSeconds}s before, {postRecordSeconds}s after.
          </Typography>
          <Typography variant="body2" color="text.secondary">
            {alarmStartTs ? `Alarm started at ${alarmStartTs}` : 'Alarm is not active in this test panel.'}
          </Typography>

          <TextField
            label="Timeout, sec"
            type="number"
            value={timeoutSeconds}
            onChange={(event) => setNonNegativeNumber(event.target.value, setTimeoutSeconds)}
            size="small"
            fullWidth
            slotProps={{ htmlInput: { min: 0 } }}
          />

          <Stack direction="row" spacing={1}>
            <Button variant="contained" disabled={!selectedCameraId} onClick={() => void sendAlarmStart()} fullWidth>
              Alarm start now
            </Button>
            <Button
              variant="outlined"
              disabled={!selectedCameraId || !alarmStartTs}
              onClick={() => void sendAlarmEnd()}
              fullWidth
            >
              Alarm end now
            </Button>
          </Stack>
        </Stack>
      </CardContent>
    </Card>
  )
}
