import { useState } from 'react'
import { Button, MenuItem, Stack, TextField, Typography } from '@mui/material'
import { useAppDispatch, useAppSelector } from '../store'
import { addCamera, changeCameraParent, removeCamera } from '../store/liveSlice'
import type { CameraSetupDto } from '../dto/dtos'
import CameraStoragesControl from './CameraStoragesControl'
import PasswordField from './PasswordField'

type Props = {
  cameraId: string  // '' = new camera
  serverId: string  // pre-selected server
  onSaved: () => Promise<void>
  onRemoved: () => Promise<void>
}

export default function CameraSetupPanel({ cameraId: initialCameraId, serverId: initialServerId, onSaved, onRemoved }: Props) {
  const dispatch = useAppDispatch()
  const servers = useAppSelector((state) => state.live.servers)
  const camerasMap = useAppSelector((state) => state.live.cameraStatesById)
  const existing = camerasMap[initialCameraId]
  const isNew = initialCameraId === ''

  const [cameraId, setCameraId] = useState(initialCameraId)
  const [serverId, setServerId] = useState(initialServerId)
  const [user, setUser] = useState(existing?.cred?.user ?? '')
  const [pass, setPass] = useState(existing?.cred?.pass ?? '')
  const [onvifIp, setOnvifIp] = useState(existing?.onvifConn?.ip ?? '')
  const [onvifPort, setOnvifPort] = useState(existing?.onvifConn?.port ?? 0)
  const [preRecordSeconds, setPreRecordSeconds] = useState(existing?.preAlarm?.preRecordSeconds ?? 30)
  const [postRecordSeconds, setPostRecordSeconds] = useState(existing?.preAlarm?.postRecordSeconds ?? 30)
  const [preAlarmSegmentSeconds, setPreAlarmSegmentSeconds] = useState(existing?.preAlarm?.segmentSeconds ?? 5)

  const setNonNegativeNumber = (raw: string, setter: (n: number) => void) => {
    const value = Number(raw)
    if (!Number.isNaN(value)) setter(Math.max(0, value))
  }

  const buildPayload = (): CameraSetupDto => ({
    cameraId: isNew ? cameraId : initialCameraId,
    serverId,
    recordingMode: existing?.recordingMode ?? 'off',
    webRtc: existing?.webRtc ?? false,
    rtspUrl: existing?.rtspUrl ?? '',
    streams: existing?.streams ?? [],
    cred: user || pass ? { user, pass } : undefined,
    onvifConn: onvifIp ? { ip: onvifIp, port: onvifPort } : undefined,
    preAlarm: { preRecordSeconds, postRecordSeconds, segmentSeconds: preAlarmSegmentSeconds }
  })

  const handleSave = async (e: React.FormEvent) => {
    e.preventDefault()
    const payload = buildPayload()
    const serverChanged = !isNew && existing && serverId !== existing.serverId
    const action = serverChanged ? changeCameraParent : addCamera
    const result = await dispatch(action(payload))
    if (!action.rejected.match(result)) {
      await onSaved()
    }
  }

  const handleRemove = async () => {
    const result = await dispatch(removeCamera({ cameraId: initialCameraId }))
    if (!removeCamera.rejected.match(result)) {
      await onRemoved()
    }
  }

  return (
    <Stack component="form" spacing={2} onSubmit={handleSave}>
      <Typography variant="subtitle2" color="text.secondary">
        {isNew ? 'New camera' : `Camera: ${initialCameraId}`}
      </Typography>

      <TextField
        label="Camera ID"
        value={isNew ? cameraId : initialCameraId}
        onChange={(e) => setCameraId(e.target.value)}
        disabled={!isNew}
        required
        size="small"
      />

      <TextField select label="Server" value={serverId} onChange={(e) => setServerId(e.target.value)} size="small">
        {servers.map((s) => (
          <MenuItem key={s.serverId} value={s.serverId}>{s.serverId}</MenuItem>
        ))}
      </TextField>

      <Stack direction="row" spacing={1}>
        <TextField label="ONVIF IP" value={onvifIp} onChange={(e) => setOnvifIp(e.target.value)} size="small" />
        <TextField
          label="ONVIF Port"
          value={onvifPort}
          onChange={(e) => setNonNegativeNumber(e.target.value, setOnvifPort)}
          size="small"
          sx={{ width: 120 }}
        />
      </Stack>

      <Stack direction="row" spacing={1}>
        <TextField label="User" value={user} onChange={(e) => setUser(e.target.value)} size="small" />
        <PasswordField pass={pass} setPass={setPass} />
      </Stack>

      <Stack direction="row" spacing={1}>
        <TextField
          label="Pre-record, sec"
          type="number"
          value={preRecordSeconds}
          onChange={(e) => setNonNegativeNumber(e.target.value, setPreRecordSeconds)}
          size="small"
          slotProps={{ htmlInput: { min: 0 } }}
        />
        <TextField
          label="Post-record, sec"
          type="number"
          value={postRecordSeconds}
          onChange={(e) => setNonNegativeNumber(e.target.value, setPostRecordSeconds)}
          size="small"
          slotProps={{ htmlInput: { min: 0 } }}
        />
        <TextField
          label="Segment, sec"
          type="number"
          value={preAlarmSegmentSeconds}
          onChange={(e) => setNonNegativeNumber(e.target.value, setPreAlarmSegmentSeconds)}
          size="small"
          slotProps={{ htmlInput: { min: 1 } }}
        />
      </Stack>

      <Stack direction="row" spacing={1}>
        <Button type="submit" variant="outlined" size="small">
          {isNew ? 'Add camera' : 'Save'}
        </Button>
        {!isNew && (
          <Button color="error" variant="outlined" size="small" onClick={handleRemove}>
            Delete camera
          </Button>
        )}
      </Stack>

      {!isNew && <CameraStoragesControl cameraId={initialCameraId} />}
    </Stack>
  )
}
