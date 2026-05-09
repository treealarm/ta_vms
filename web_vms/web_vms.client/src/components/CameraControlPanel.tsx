import type { Dispatch, FormEventHandler, SetStateAction } from 'react'
import { useState } from 'react'
import {
  Box,
  Button,
  Card,
  CardContent,
  Collapse,
  List,
  ListItemButton,
  ListItemText,
  MenuItem,
  Stack,
  TextField
} from '@mui/material'

import PasswordField from './PasswordField'
import CameraStoragesControl from './CameraStoragesControl'
import type { CameraStateDto, ServerInstanceDto } from '../dto/dtos'

type CameraControlPanelProps = {
  cameraId: string
  setCameraId: Dispatch<SetStateAction<string>>
  rtspUrl: string
  setRtspUrl: Dispatch<SetStateAction<string>>
  user: string
  setUser: Dispatch<SetStateAction<string>>
  pass: string
  setPass: Dispatch<SetStateAction<string>>
  onvifIp: string
  setOnvifIp: Dispatch<SetStateAction<string>>
  onvifPort: number
  setOnvifPort: Dispatch<SetStateAction<number>>
  preRecordSeconds: number
  setPreRecordSeconds: Dispatch<SetStateAction<number>>
  postRecordSeconds: number
  setPostRecordSeconds: Dispatch<SetStateAction<number>>
  preAlarmSegmentSeconds: number
  setPreAlarmSegmentSeconds: Dispatch<SetStateAction<number>>
  selectedServerId: string
  setSelectedServerId: Dispatch<SetStateAction<string>>
  selectedCameraId: string
  setSelectedCameraId: Dispatch<SetStateAction<string>>
  servers: ServerInstanceDto[]
  isLimitReached: boolean
  onAddCamera: FormEventHandler
  onRefreshLists: () => void | Promise<void>
  onRemoveCamera: (cameraId: string) => void | Promise<void>
  onMoveCamera: (cameraId: string, serverId: string) => void | Promise<void>
  onSelectCamera: (camera: CameraStateDto) => void
}

export default function CameraControlPanel({
  cameraId,
  setCameraId,
  rtspUrl,
  setRtspUrl,
  user,
  setUser,
  pass,
  setPass,
  onvifIp,
  setOnvifIp,
  onvifPort,
  setOnvifPort,
  preRecordSeconds,
  setPreRecordSeconds,
  postRecordSeconds,
  setPostRecordSeconds,
  preAlarmSegmentSeconds,
  setPreAlarmSegmentSeconds,
  selectedServerId,
  setSelectedServerId,
  selectedCameraId,
  setSelectedCameraId,
  servers,
  isLimitReached,
  onAddCamera,
  onRefreshLists,
  onRemoveCamera,
  onMoveCamera,
  onSelectCamera
}: CameraControlPanelProps) {
  const [isVisible, setIsVisible] = useState(true)

  const setNonNegativeNumber = (raw: string, setter: Dispatch<SetStateAction<number>>) => {
    const value = Number(raw)
    if (!Number.isNaN(value)) {
      setter(Math.max(0, value))
    }
  }

  return (
    <Box>
      {!isVisible && (
        <Button variant="contained" size="small" onClick={() => setIsVisible(true)}>
          Show camera controls
        </Button>
      )}

      <Collapse in={isVisible} timeout={220} unmountOnExit>
        <Card>
          <CardContent>
            <Box component="form" onSubmit={onAddCamera}>
              <Stack spacing={2}>
                <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap">
                  <Button size="small" onClick={() => setIsVisible(false)}>
                    Hide
                  </Button>
                </Stack>

                <Stack direction={{ xs: 'column', md: 'row' }} spacing={2}>
                  <Box sx={{ minWidth: 280, border: 1, borderColor: 'divider', borderRadius: 1 }}>
                    <List dense>
                      {servers.map((server) => (
                        <Box key={server.serverId}>
                          <ListItemButton
                            selected={selectedServerId === server.serverId && selectedCameraId === ''}
                            onClick={() => {
                              setSelectedServerId(server.serverId)
                              setSelectedCameraId('')
                            }}
                          >
                            <ListItemText primary={server.serverId} secondary={server.address} />
                          </ListItemButton>
                          {server.cameras.map((camera) => (
                            <ListItemButton
                              key={camera.cameraId}
                              sx={{ pl: 4 }}
                              selected={selectedCameraId === camera.cameraId}
                              onClick={() => {
                                setSelectedServerId(server.serverId)
                                setSelectedCameraId(camera.cameraId)
                                onSelectCamera(camera)
                              }}
                            >
                              <ListItemText primary={camera.cameraId} />
                            </ListItemButton>
                          ))}
                        </Box>
                      ))}
                    </List>
                  </Box>

                  <Stack spacing={2} flex={1}>
                    <TextField
                      select
                      label="Server"
                      value={selectedServerId}
                      onChange={(event) => {
                        const nextServerId = event.target.value
                        if (selectedCameraId) {
                          void onMoveCamera(selectedCameraId, nextServerId)
                        }
                        setSelectedServerId(nextServerId)
                      }}
                      size="small"
                    >
                      {servers.map((server) => (
                        <MenuItem key={server.serverId} value={server.serverId}>
                          {server.serverId} ({server.address})
                        </MenuItem>
                      ))}
                    </TextField>

                    <TextField
                      label="Camera ID"
                      value={cameraId}
                      onChange={(event) => setCameraId(event.target.value)}
                      required
                      fullWidth
                      size="small"
                    />

                    <Stack direction="row" spacing={1.5} alignItems="center">
                      <TextField
                        label="RTSP URL"
                        value={rtspUrl}
                        onChange={(event) => setRtspUrl(event.target.value)}
                        fullWidth
                        size="small"
                      />

                      <TextField label="User" value={user} onChange={(event) => setUser(event.target.value)} fullWidth size="small" />
                      <PasswordField pass={pass} setPass={setPass} />

                      <Button
                        disabled={isLimitReached || selectedServerId === ''}
                        type="submit"
                        variant="outlined"
                        size="small"
                        sx={{ minWidth: 120 }}
                      >
                        Setup
                      </Button>
                    </Stack>
                    <Stack direction="row" spacing={1.5} alignItems="center">
                      <TextField label="Onvif IP" value={onvifIp} onChange={(event) => setOnvifIp(event.target.value)} size="small" />
                      <TextField
                        label="Onvif Port"
                        value={onvifPort}
                        onChange={(event) => setNonNegativeNumber(event.target.value, setOnvifPort)}
                        size="small"
                      />
                      <Button
                        disabled={!selectedCameraId}
                        color="error"
                        variant="outlined"
                        onClick={() => void onRemoveCamera(selectedCameraId)}
                      >
                        Delete selected
                      </Button>
                    </Stack>
                    <Stack direction="row" spacing={1.5} alignItems="center">
                      <TextField
                        label="Pre-record, sec"
                        type="number"
                        value={preRecordSeconds}
                        onChange={(event) => setNonNegativeNumber(event.target.value, setPreRecordSeconds)}
                        size="small"
                        slotProps={{ htmlInput: { min: 0 } }}
                      />
                      <TextField
                        label="Post-record, sec"
                        type="number"
                        value={postRecordSeconds}
                        onChange={(event) => setNonNegativeNumber(event.target.value, setPostRecordSeconds)}
                        size="small"
                        slotProps={{ htmlInput: { min: 0 } }}
                      />
                      <TextField
                        label="Pre-alarm segment, sec"
                        type="number"
                        value={preAlarmSegmentSeconds}
                        onChange={(event) => setNonNegativeNumber(event.target.value, setPreAlarmSegmentSeconds)}
                        size="small"
                        slotProps={{ htmlInput: { min: 1 } }}
                      />
                    </Stack>
                  </Stack>
                </Stack>

                <Stack direction="row" spacing={1.5}>
                  <Button variant="outlined" onClick={() => void onRefreshLists()} fullWidth>
                    Refresh list
                  </Button>
                </Stack>

                {selectedCameraId && (
                  <CameraStoragesControl cameraId={selectedCameraId} />
                )}
              </Stack>
            </Box>
          </CardContent>
        </Card>
      </Collapse>
    </Box>
  )
}
