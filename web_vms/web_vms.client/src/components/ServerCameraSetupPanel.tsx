import { useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'
import {
  Box,
  Button,
  Card,
  CardContent,
  FormControl,
  InputLabel,
  List,
  ListItemButton,
  ListItemText,
  MenuItem,
  Select,
  Stack,
  TextField,
  Typography
} from '@mui/material'
import ExpandLessIcon from '@mui/icons-material/ExpandLess'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'

import type { CameraStateDto, MediaServerNodeDto } from '../dto/dtos'
import { addCamera, changeCameraParent, setLiveStatus } from '../store/liveSlice'
import { useAppDispatch } from '../store'
import PasswordField from './PasswordField'

type Props = {
  cameras: CameraStateDto[]
  servers: MediaServerNodeDto[]
  isLimitReached: boolean
  maxCameras: number
  onRefresh: () => Promise<void>
}

const DEFAULT_RTSP_URL = 'rtsp://192.168.1.150:554/channel=0_stream=0&onvif=0.sdp?real_stream'
const DEFAULT_USER = 'orwell'
const DEFAULT_PASS = 'elvees'
const DEFAULT_ONVIF_IP = '192.168.1.150'
const DEFAULT_ONVIF_PORT = 8899

const getNextCameraId = (cameras: CameraStateDto[]) => {
  const existing = new Set(cameras.map((camera) => camera.cameraId))
  let index = 1

  while (existing.has(`cam${index}`)) {
    index += 1
  }

  return `cam${index}`
}

export default function ServerCameraSetupPanel({
  cameras,
  servers,
  isLimitReached,
  maxCameras,
  onRefresh
}: Props) {
  const dispatch = useAppDispatch()
  const [selectedCameraId, setSelectedCameraId] = useState<string | null>(null)
  const [serverId, setServerId] = useState('')
  const [cameraId, setCameraId] = useState(() => getNextCameraId(cameras))
  const [rtspUrl, setRtspUrl] = useState(DEFAULT_RTSP_URL)
  const [user, setUser] = useState(DEFAULT_USER)
  const [pass, setPass] = useState(DEFAULT_PASS)
  const [onvifIp, setOnvifIp] = useState(DEFAULT_ONVIF_IP)
  const [onvifPort, setOnvifPort] = useState(DEFAULT_ONVIF_PORT)
  const [expanded, setExpanded] = useState(true)

  const serverOptions = useMemo(() => {
    const ids = new Set<string>()
    servers.forEach((server) => ids.add(server.serverId))
    cameras.forEach((camera) => {
      if (camera.serverId) {
        ids.add(camera.serverId)
      }
    })
    return Array.from(ids).sort()
  }, [cameras, servers])

  const selectedCamera = useMemo(
    () => cameras.find((camera) => camera.cameraId === selectedCameraId) ?? null,
    [cameras, selectedCameraId]
  )

  const camerasByServer = useMemo(() => {
    const grouped = new Map<string, CameraStateDto[]>()
    serverOptions.forEach((id) => grouped.set(id, []))

    for (const camera of cameras) {
      const parent = camera.serverId || ''
      grouped.set(parent, [...(grouped.get(parent) ?? []), camera])
    }

    return grouped
  }, [cameras, serverOptions])

  useEffect(() => {
    if (!serverOptions.includes(serverId)) {
      setServerId(serverOptions[0] ?? '')
    }
  }, [serverId, serverOptions])

  const fillFromCamera = (camera: CameraStateDto) => {
    setSelectedCameraId(camera.cameraId)
    setCameraId(camera.cameraId)
    setServerId(camera.serverId || '')
    setRtspUrl(camera.rtspUrl ?? '')
    setUser(camera.cred?.user ?? '')
    setPass(camera.cred?.pass ?? '')
    setOnvifIp(camera.onvifConn?.ip ?? '')
    setOnvifPort(camera.onvifConn?.port ?? 0)
  }

  const fillDefaultsForServer = (id: string) => {
    setSelectedCameraId(null)
    setServerId(id)
    setCameraId(getNextCameraId(cameras))
    setRtspUrl(DEFAULT_RTSP_URL)
    setUser(DEFAULT_USER)
    setPass(DEFAULT_PASS)
    setOnvifIp(DEFAULT_ONVIF_IP)
    setOnvifPort(DEFAULT_ONVIF_PORT)
  }

  const selectServer = (id: string) => {
    fillDefaultsForServer(id)
  }

  const onServerSelectChange = (id: string) => {
    if (selectedCamera) {
      setServerId(id)
      return
    }

    fillDefaultsForServer(id)
  }

  const buildPayload = (): CameraStateDto => ({
    cameraId,
    serverId,
    rtspUrl,
    recording: selectedCamera?.recording ?? false,
    webRtc: selectedCamera?.webRtc ?? true,
    cred: user || pass
      ? {
        user,
        pass
      }
      : undefined,
    onvifConn: onvifIp
      ? {
        ip: onvifIp,
        port: onvifPort
      }
      : undefined
  })

  const isExistingCamera = selectedCamera?.cameraId === cameraId
  const isParentChanged = Boolean(isExistingCamera && selectedCamera?.serverId && selectedCamera.serverId !== serverId)
  const canSetup = Boolean(serverId) && !isParentChanged && (!isLimitReached || isExistingCamera)

  const onSetup = async (event: FormEvent) => {
    event.preventDefault()
    if (!canSetup) {
      dispatch(setLiveStatus(
        serverId
          ? `Error: Maximum ${maxCameras} cameras supported in grid view`
          : 'Error: Select a registered media server first'
      ))
      return
    }

    const result = await dispatch(addCamera(buildPayload()))
    if (!addCamera.rejected.match(result)) {
      await onRefresh()
    }
  }

  const onChangeParent = async () => {
    const result = await dispatch(changeCameraParent(buildPayload()))
    if (!changeCameraParent.rejected.match(result)) {
      await onRefresh()
    }
  }

  return (
    <Card>
      <CardContent>
        <Stack spacing={2}>
          <Stack direction="row" alignItems="center" justifyContent="space-between">
            <Typography variant="subtitle1">Camera setup</Typography>
            <Button
              onClick={() => setExpanded((value) => !value)}
              size="small"
              startIcon={expanded ? <ExpandLessIcon /> : <ExpandMoreIcon />}
            >
              {expanded ? 'Hide' : 'Show'}
            </Button>
          </Stack>

          {expanded && (
            <Stack direction={{ xs: 'column', md: 'row' }} spacing={2}>
          <Box sx={{ width: { xs: '100%', md: 300 }, flexShrink: 0 }}>
            <Stack spacing={1}>
              <Typography variant="subtitle1">Media servers</Typography>
              <List dense sx={{ border: 1, borderColor: 'divider', borderRadius: 1 }}>
                {serverOptions.map((id) => {
                  const serverCameras = camerasByServer.get(id) ?? []

                  return (
                    <Box key={id}>
                      <ListItemButton selected={selectedCameraId == null && serverId === id} onClick={() => selectServer(id)}>
                        <ListItemText primary={id} />
                      </ListItemButton>
                      {serverCameras.map((camera) => (
                        <ListItemButton
                          key={camera.cameraId}
                          selected={selectedCameraId === camera.cameraId}
                          onClick={() => fillFromCamera(camera)}
                          sx={{ pl: 4 }}
                        >
                          <ListItemText primary={camera.cameraId} />
                        </ListItemButton>
                      ))}
                    </Box>
                  )
                })}
              </List>
            </Stack>
          </Box>

          <Box component="form" onSubmit={onSetup} sx={{ flex: 1 }}>
            <Stack spacing={2}>
              <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5}>
                <FormControl size="small" fullWidth>
                  <InputLabel id="camera-server-label">Server</InputLabel>
                  <Select
                    labelId="camera-server-label"
                    label="Server"
                    value={serverId}
                    onChange={(event) => onServerSelectChange(event.target.value)}
                  >
                    {!serverId && (
                      <MenuItem value="" disabled>Select server</MenuItem>
                    )}
                    {serverOptions.map((id) => (
                      <MenuItem key={id} value={id}>{id}</MenuItem>
                    ))}
                  </Select>
                </FormControl>
                <TextField
                  label="Camera ID"
                  value={cameraId}
                  onChange={(event) => setCameraId(event.target.value)}
                  required
                  fullWidth
                  size="small"
                />
              </Stack>

              <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5} alignItems="center">
                <TextField
                  label="RTSP URL"
                  value={rtspUrl}
                  onChange={(event) => setRtspUrl(event.target.value)}
                  fullWidth
                  size="small"
                />
                <TextField
                  label="User"
                  value={user}
                  onChange={(event) => setUser(event.target.value)}
                  fullWidth
                  size="small"
                />
                <PasswordField pass={pass} setPass={setPass} />
              </Stack>

              <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5} alignItems="center">
                <TextField
                  label="Onvif IP"
                  value={onvifIp}
                  onChange={(event) => setOnvifIp(event.target.value)}
                  size="small"
                />
                <TextField
                  label="Onvif Port"
                  value={onvifPort}
                  onChange={(event) => {
                    const value = Number(event.target.value)
                    if (!Number.isNaN(value)) {
                      setOnvifPort(value)
                    }
                  }}
                  size="small"
                />
              </Stack>

              {isParentChanged && (
                <Typography color="warning.main" variant="body2">
                  Server changed for an existing camera. Use Change parent to move it between media server instances.
                </Typography>
              )}
              {!serverId && (
                <Typography color="warning.main" variant="body2">
                  No registered media server selected. Start a media server first, then refresh the list.
                </Typography>
              )}

              <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5}>
                <Button disabled={!canSetup} type="submit" variant="outlined" fullWidth>
                  Setup
                </Button>
                <Button disabled={!serverId || !isParentChanged} onClick={() => void onChangeParent()} variant="contained" fullWidth>
                  Change parent
                </Button>
                <Button variant="outlined" onClick={() => void onRefresh()} fullWidth>
                  Refresh list
                </Button>
              </Stack>
            </Stack>
          </Box>
            </Stack>
          )}
        </Stack>
      </CardContent>
    </Card>
  )
}
