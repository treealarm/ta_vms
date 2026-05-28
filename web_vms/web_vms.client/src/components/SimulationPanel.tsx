import { useState } from 'react'
import {
  Alert,
  Button,
  Card,
  CardContent,
  Divider,
  MenuItem,
  Stack,
  TextField,
  Typography
} from '@mui/material'
import { useTranslation } from 'react-i18next'

import type { CameraSetupDto, RetentionEventDto } from '../dto/dtos'
import { useAppDispatch, useAppSelector } from '../store'
import { sendRetentionEvent, setSimulationAlarmStartTs, setSimulationEventId } from '../store/fsSlice'
import { fetchCameraState } from '../store/liveSlice'

type SimulationPanelProps = {
  cameras: CameraSetupDto[]
}

const generateEventId = (cameraId: string) =>
  `test-alarm-${cameraId || 'camera'}-${Date.now()}`

export default function SimulationPanel({ cameras }: SimulationPanelProps) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const eventId = useAppSelector((s) => s.fs.simulationEventId)
  const alarmStartTs = useAppSelector((s) => s.fs.simulationAlarmStartTs)
  const simulationResult = useAppSelector((s) => s.fs.simulationResult)
  const [cameraId, setCameraId] = useState('')
  const [streamName, setStreamName] = useState('')
  const [timeoutSeconds, setTimeoutSeconds] = useState(30)

  const selectedCameraId = cameraId || cameras[0]?.cameraId || ''
  const selectedCamera = cameras.find((c) => c.cameraId === selectedCameraId)
  const recordableStreams = selectedCamera?.streams ?? []

  const setNonNegativeNumber = (raw: string, setter: (value: number) => void) => {
    const value = Number(raw)
    if (!Number.isNaN(value)) setter(Math.max(0, value))
  }

  const newEventId = () => {
    dispatch(setSimulationEventId(generateEventId(selectedCameraId)))
    dispatch(setSimulationAlarmStartTs(''))
  }

  const sendAlarmStart = async () => {
    if (!selectedCameraId) return
    const now = new Date().toISOString()
    dispatch(setSimulationAlarmStartTs(now))

    const payload: RetentionEventDto = {
      eventId,
      cameraId: selectedCameraId,
      streamName: streamName || undefined,
      alarmStartTs: now,
      timeoutSeconds,
    }
    await dispatch(sendRetentionEvent(payload))
    void dispatch(fetchCameraState(selectedCameraId))
  }

  const sendAlarmEnd = async () => {
    if (!selectedCameraId || !alarmStartTs) return
    const payload: RetentionEventDto = {
      eventId,
      cameraId: selectedCameraId,
      streamName: streamName || undefined,
      alarmStartTs,
      alarmEndTs: new Date().toISOString(),
      timeoutSeconds,
    }
    await dispatch(sendRetentionEvent(payload))
    void dispatch(fetchCameraState(selectedCameraId))
    dispatch(setSimulationAlarmStartTs(''))
    dispatch(setSimulationEventId(generateEventId(selectedCameraId)))
  }

  return (
    <Card elevation={2} sx={{ maxWidth: 480 }}>
      <CardContent>
        <Stack spacing={2}>
          <Typography variant="h6">{t('simulation.title')}</Typography>

          <Divider />

          <Typography variant="subtitle2">{t('simulation.retentionEvent')}</Typography>

          <TextField
            select
            label={t('simulation.camera')}
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
            select
            label={t('simulation.stream')}
            value={streamName}
            onChange={(e) => setStreamName(e.target.value)}
            size="small"
            fullWidth
            helperText={t('simulation.streamHelper')}
          >
            <MenuItem value="">{t('simulation.allStreams')}</MenuItem>
            {recordableStreams.map((s) => (
              <MenuItem key={s.streamName} value={s.streamName}>{s.streamName}</MenuItem>
            ))}
          </TextField>

          <TextField
            label={t('simulation.eventId')}
            value={eventId}
            onChange={(e) => dispatch(setSimulationEventId(e.target.value))}
            helperText={t('simulation.eventIdHelper')}
            size="small"
            fullWidth
          />

          <Button variant="outlined" onClick={newEventId}>
            {t('simulation.generateEventId')}
          </Button>

          <Typography variant="body2" color="text.secondary">
            {alarmStartTs ? t('simulation.alarmActive', { time: alarmStartTs }) : t('simulation.alarmInactive')}
          </Typography>

          <TextField
            label={t('simulation.timeoutSec')}
            type="number"
            value={timeoutSeconds}
            onChange={(event) => setNonNegativeNumber(event.target.value, setTimeoutSeconds)}
            size="small"
            fullWidth
            slotProps={{ htmlInput: { min: 0 } }}
          />

          <Stack direction="row" spacing={1}>
            <Button variant="contained" disabled={!selectedCameraId} onClick={() => void sendAlarmStart()} fullWidth>
              {t('simulation.alarmStart')}
            </Button>
            <Button
              variant="outlined"
              disabled={!selectedCameraId || !alarmStartTs}
              onClick={() => void sendAlarmEnd()}
              fullWidth
            >
              {t('simulation.alarmEnd')}
            </Button>
          </Stack>

          {simulationResult && (
            <Alert severity={simulationResult.ok ? 'success' : 'error'} sx={{ wordBreak: 'break-word' }}>
              {simulationResult.message}
            </Alert>
          )}
        </Stack>
      </CardContent>
    </Card>
  )
}
