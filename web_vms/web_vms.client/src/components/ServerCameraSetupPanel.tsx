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
import { useTranslation } from 'react-i18next'

import type { CameraSetupDto, MediaServerNodeDto } from '../dto/dtos'
import { addCamera, changeCameraParent, setLiveStatus } from '../store/liveSlice'
import { useAppDispatch } from '../store'
import PasswordField from './PasswordField'
import CameraStoragesControl from './CameraStoragesControl'

type Props = {
  cameras: CameraSetupDto[]
  servers: MediaServerNodeDto[]
  isLimitReached: boolean
  maxCameras: number
  onRefresh: () => Promise<void>
}

const DEFAULT_PATH = 'rtsp://192.168.1.150:554/channel=0_stream=0&onvif=0.sdp?real_stream'
const DEFAULT_USER = 'orwell'
const DEFAULT_PASS = 'elvees'
const DEFAULT_IP = '192.168.1.150'
const DEFAULT_PORT = 8899

export default function ServerCameraSetupPanel({
  cameras,
  servers,
  isLimitReached,
  maxCameras,
  onRefresh
}: Props) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const [selectedCameraId, setSelectedCameraId] = useState<string | null>(null)
  const [serverId, setServerId] = useState('')
  const [cameraId, setCameraId] = useState(() => crypto.randomUUID())
  const [name, setName] = useState('')
  const [path, setPath] = useState(DEFAULT_PATH)
  const [user, setUser] = useState(DEFAULT_USER)
  const [pass, setPass] = useState(DEFAULT_PASS)
  const [ip, setIp] = useState(DEFAULT_IP)
  const [port, setPort] = useState(DEFAULT_PORT)
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
    const grouped = new Map<string, CameraSetupDto[]>()
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

  const fillFromCamera = (camera: CameraSetupDto) => {
    setSelectedCameraId(camera.cameraId)
    setCameraId(camera.cameraId)
    setName(camera.name)
    setServerId(camera.serverId || '')
    setPath(camera.path ?? '')
    setUser(camera.cred?.user ?? '')
    setPass(camera.cred?.pass ?? '')
    setIp(camera.onvifConn?.ip ?? '')
    setPort(camera.onvifConn?.port ?? 0)
  }

  const fillDefaultsForServer = (id: string) => {
    setSelectedCameraId(null)
    setServerId(id)
    setCameraId(crypto.randomUUID())
    setName('')
    setPath(DEFAULT_PATH)
    setUser(DEFAULT_USER)
    setPass(DEFAULT_PASS)
    setIp(DEFAULT_IP)
    setPort(DEFAULT_PORT)
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

  const buildPayload = (): CameraSetupDto => ({
    cameraId,
    name,
    serverId,
    recordingMode: selectedCamera?.recordingMode ?? 'off',
    recording: selectedCamera?.recording ?? false,
    webRtc: selectedCamera?.webRtc ?? true,
    path,
    cred: user || pass
      ? {
        user,
        pass
      }
      : undefined,
    onvifConn: ip
      ? {
        ip,
        port
      }
      : undefined,
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
            <Typography variant="subtitle1">{t('serverSetup.title')}</Typography>
            <Button
              onClick={() => setExpanded((value) => !value)}
              size="small"
              startIcon={expanded ? <ExpandLessIcon /> : <ExpandMoreIcon />}
            >
              {expanded ? t('serverSetup.hide') : t('serverSetup.show')}
            </Button>
          </Stack>

          {expanded && (
            <Stack direction={{ xs: 'column', md: 'row' }} spacing={2}>
          <Box sx={{ width: { xs: '100%', md: 300 }, flexShrink: 0 }}>
            <Stack spacing={1}>
              <Typography variant="subtitle1">{t('serverSetup.mediaServers')}</Typography>
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
                          <ListItemText primary={camera.name || camera.cameraId} />
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
                  <InputLabel id="camera-server-label">{t('serverSetup.server')}</InputLabel>
                  <Select
                    labelId="camera-server-label"
                    label={t('serverSetup.server')}
                    value={serverId}
                    onChange={(event) => onServerSelectChange(event.target.value)}
                  >
                    {!serverId && (
                      <MenuItem value="" disabled>{t('serverSetup.selectServer')}</MenuItem>
                    )}
                    {serverOptions.map((id) => (
                      <MenuItem key={id} value={id}>{id}</MenuItem>
                    ))}
                  </Select>
                </FormControl>
                <TextField
                  label={t('cameras.cameraName')}
                  value={name}
                  onChange={(event) => setName(event.target.value)}
                  required
                  fullWidth
                  size="small"
                />
              </Stack>

              <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5} alignItems="center">
                <TextField
                  label={t('serverSetup.path')}
                  value={path}
                  onChange={(event) => setPath(event.target.value)}
                  fullWidth
                  size="small"
                />
                <TextField
                  label={t('common.user')}
                  value={user}
                  onChange={(event) => setUser(event.target.value)}
                  fullWidth
                  size="small"
                />
                <PasswordField pass={pass} setPass={setPass} />
              </Stack>

              <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5} alignItems="center">
                <TextField
                  label={t('serverSetup.ip')}
                  value={ip}
                  onChange={(event) => setIp(event.target.value)}
                  size="small"
                />
                <TextField
                  label={t('serverSetup.port')}
                  value={port}
                  onChange={(event) => {
                    const value = Number(event.target.value)
                    if (!Number.isNaN(value)) {
                      setPort(value)
                    }
                  }}
                  size="small"
                />
              </Stack>

              {isParentChanged && (
                <Typography color="warning.main" variant="body2">
                  {t('serverSetup.parentChanged')}
                </Typography>
              )}
              {!serverId && (
                <Typography color="warning.main" variant="body2">
                  {t('serverSetup.noServer')}
                </Typography>
              )}

              <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5}>
                <Button disabled={!canSetup} type="submit" variant="outlined" fullWidth>
                  {t('serverSetup.setup')}
                </Button>
                <Button disabled={!serverId || !isParentChanged} onClick={() => void onChangeParent()} variant="contained" fullWidth>
                  {t('serverSetup.changeParent')}
                </Button>
                <Button variant="outlined" onClick={() => void onRefresh()} fullWidth>
                  {t('serverSetup.refreshList')}
                </Button>
              </Stack>

              {selectedCameraId && (
                <CameraStoragesControl cameraId={selectedCameraId} />
              )}
            </Stack>
          </Box>
            </Stack>
          )}
        </Stack>
      </CardContent>
    </Card>
  )
}
