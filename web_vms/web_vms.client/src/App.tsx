import { useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'
import {
  Alert,
  Box,
  Card,
  CardContent,
  Stack,
  Tab,
  Tabs
} from '@mui/material'

import { type CameraStateDto, type ServerInstanceDto } from './dto/dtos'
import {
  addCamera,
  changeCameraParent,
  loadCameras,
  loadServers,
  removeCamera,
  setLiveStatus
} from './store/liveSlice'
import { useAppDispatch, useAppSelector } from './store'
import CameraUnifiedCard from './components/CameraUnifiedCard'
import CameraControlPanel from './components/CameraControlPanel'
import LogJournalPanel from './components/LogJournalPanel'
import SimulationPanel from './components/SimulationPanel'
import RetentionEventsTab from './components/RetentionEventsTab'

const MAX_CAMERAS = 4

export default function App() {
  const dispatch = useAppDispatch()
  const [mainTab, setMainTab] = useState(0)
  const [cameraId, setCameraId] = useState('cam1')
  const [rtspUrl, setRtspUrl] = useState('rtsp://192.168.1.150:554/channel=0_stream=0&onvif=0.sdp?real_stream')
  const [user, setUser] = useState('orwell')
  const [pass, setPass] = useState('elvees')
  const [onvifIp, setOnvifIp] = useState('192.168.1.150')
  const [onvifPort, setOnvifPort] = useState(8899)
  const [preRecordSeconds, setPreRecordSeconds] = useState(10)
  const [postRecordSeconds, setPostRecordSeconds] = useState(10)
  const [preAlarmSegmentSeconds, setPreAlarmSegmentSeconds] = useState(5)
  const [selectedServerId, setSelectedServerId] = useState('')
  const [selectedCameraId, setSelectedCameraId] = useState('')
  const [soloCameraId, setSoloCameraId] = useState<string | null>(null)

  const status = useAppSelector((state) =>
    state.live.status !== 'Ready' ? state.live.status : state.fs.status
  )

  const camerasMap = useAppSelector((state) => state.live.cameraStatesById)
  const servers = useAppSelector((state) => state.live.servers)

  const serversTree: ServerInstanceDto[] = useMemo(() => {
    const cams = Object.values(camerasMap)
    return servers.map((s) => ({
      serverId: s.serverId,
      address: s.status ? `${s.serverId} (${s.status})` : s.serverId,
      cameras: cams.filter((c) => c.serverId === s.serverId)
    }))
  }, [servers, camerasMap])

  const { visibleCameras, total } = useMemo(() => {
    const arr = Object.values(camerasMap)
    return {
      visibleCameras: arr.slice(0, MAX_CAMERAS),
      total: arr.length
    }
  }, [camerasMap])

  const gridCameras = useMemo(() => {
    if (soloCameraId == null) {
      return visibleCameras
    }
    return visibleCameras.filter((c) => c.cameraId === soloCameraId)
  }, [soloCameraId, visibleCameras])

  const isLimitReached = total >= MAX_CAMERAS

  const monitorGridSx = useMemo(() => {
    const n = gridCameras.length
    if (n <= 0) {
      return { flex: 1, minHeight: 0, width: '100%' }
    }
    const base = {
      display: 'grid' as const,
      gap: 2,
      flex: 1,
      minHeight: 0,
      width: '100%',
      height: '100%',
      alignContent: 'stretch' as const
    }
    if (n === 1) {
      return { ...base, gridTemplateColumns: 'minmax(0, 1fr)', gridTemplateRows: 'minmax(0, 1fr)' }
    }
    if (n === 2) {
      return { ...base, gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr)', gridTemplateRows: 'minmax(0, 1fr)' }
    }
    if (n === 3) {
      return { ...base, gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr)', gridTemplateRows: 'minmax(0, 1fr) minmax(0, 1fr)' }
    }
    return { ...base, gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr)', gridTemplateRows: 'minmax(0, 1fr) minmax(0, 1fr)' }
  }, [gridCameras.length])

  useEffect(() => {
    if (mainTab !== 0) {
      setSoloCameraId(null)
    }
  }, [mainTab])

  useEffect(() => {
    if (soloCameraId != null && !visibleCameras.some((c) => c.cameraId === soloCameraId)) {
      setSoloCameraId(null)
    }
  }, [soloCameraId, visibleCameras])

  useEffect(() => {
    let cancelled = false
    let retryTimer: ReturnType<typeof setTimeout> | null = null

    const bootstrap = async (attempt: number) => {
      if (cancelled) {
        return
      }

      const [camerasResult, serversResult] = await Promise.all([
        dispatch(loadCameras()),
        dispatch(loadServers()),
      ])

      const hasError =
        loadCameras.rejected.match(camerasResult) ||
        loadServers.rejected.match(serversResult)

      if (hasError && attempt < 20 && !cancelled) {
        retryTimer = setTimeout(() => {
          void bootstrap(attempt + 1)
        }, 1500)
      }
    }

    void bootstrap(0)

    return () => {
      cancelled = true
      if (retryTimer != null) {
        clearTimeout(retryTimer)
      }
    }
  }, [dispatch])

  useEffect(() => {
    if (selectedServerId === '' && serversTree.length > 0) {
      setSelectedServerId(serversTree[0].serverId)
    }
  }, [selectedServerId, serversTree])

  const refreshLists = async () => {
    await Promise.all([dispatch(loadCameras()), dispatch(loadServers())])
  }

  const onAddCamera = async (event: FormEvent) => {
    event.preventDefault()
    if (isLimitReached) {
      dispatch(setLiveStatus(`Error: Maximum ${MAX_CAMERAS} cameras supported in grid view`))
      return
    }

    const payload: CameraStateDto = {
      cameraId,
      serverId: selectedServerId,
      rtspUrl,
      recordingMode: 'off',
      webRtc: true,
      cred: {
        user,
        pass
      },
      onvifConn: {
        ip: onvifIp,
        port: onvifPort
      },
      preAlarm: {
        preRecordSeconds,
        postRecordSeconds,
        segmentSeconds: preAlarmSegmentSeconds
      }
    }

    const result = await dispatch(addCamera(payload))
    if (!addCamera.rejected.match(result)) {
      await refreshLists()
    }
  }

  const selectCameraForEdit = (camera: CameraStateDto) => {
    setCameraId(camera.cameraId)
    setSelectedServerId(camera.serverId ?? '')
    setRtspUrl(camera.rtspUrl)
    setUser(camera.cred?.user ?? '')
    setPass(camera.cred?.pass ?? '')
    setOnvifIp(camera.onvifConn?.ip ?? '')
    setOnvifPort(camera.onvifConn?.port ?? 0)
    setPreRecordSeconds(camera.preAlarm?.preRecordSeconds ?? 30)
    setPostRecordSeconds(camera.preAlarm?.postRecordSeconds ?? 30)
    setPreAlarmSegmentSeconds(camera.preAlarm?.segmentSeconds ?? 5)
  }

  return (
    <Box sx={{ width: '100%', p: { xs: 2, md: 4 } }}>
      <Stack spacing={2.5}>
        <Tabs value={mainTab} onChange={(_, v: number) => setMainTab(v)} sx={{ borderBottom: 1, borderColor: 'divider' }}>
          <Tab label="Monitor" />
          <Tab label="Settings" />
          <Tab label="Events" />
        </Tabs>

        {mainTab === 0 && (
          <Stack spacing={2} sx={{ minHeight: 0 }}>
            <Card
              sx={{
                height: { xs: 'calc(100dvh - 11.5rem)', md: 'calc(100dvh - 14rem)' },
                maxHeight: { xs: 'calc(100dvh - 11.5rem)', md: 'calc(100dvh - 14rem)' },
                minHeight: 0,
                display: 'flex',
                flexDirection: 'column',
                overflow: 'hidden'
              }}
            >
              <CardContent
                sx={{
                  flex: 1,
                  minHeight: 0,
                  display: 'flex',
                  flexDirection: 'column',
                  overflow: 'hidden',
                  pt: 2,
                  '&:last-child': { pb: 2 }
                }}
              >
                <Box sx={monitorGridSx}>
                  {gridCameras.map((camera) => (
                    <Box
                      key={camera.cameraId}
                      sx={{
                        minWidth: 0,
                        minHeight: 0,
                        display: 'flex',
                        overflow: 'hidden'
                      }}
                    >
                      <CameraUnifiedCard
                        camera={camera}
                        gridSoloActive={soloCameraId === camera.cameraId}
                        onToggleGridSolo={
                          visibleCameras.length > 1
                            ? () =>
                                setSoloCameraId((id) => (id === camera.cameraId ? null : camera.cameraId))
                            : undefined
                        }
                      />
                    </Box>
                  ))}
                </Box>
              </CardContent>
            </Card>

            <Alert severity={status.startsWith('Error') ? 'error' : 'info'} sx={{ flexShrink: 0 }}>
              {status}
            </Alert>
          </Stack>
        )}

        {mainTab === 1 && (
          <CameraControlPanel
            cameraId={cameraId}
            setCameraId={setCameraId}
            rtspUrl={rtspUrl}
            setRtspUrl={setRtspUrl}
            user={user}
            setUser={setUser}
            pass={pass}
            setPass={setPass}
            onvifIp={onvifIp}
            setOnvifIp={setOnvifIp}
            onvifPort={onvifPort}
            setOnvifPort={setOnvifPort}
            preRecordSeconds={preRecordSeconds}
            setPreRecordSeconds={setPreRecordSeconds}
            postRecordSeconds={postRecordSeconds}
            setPostRecordSeconds={setPostRecordSeconds}
            preAlarmSegmentSeconds={preAlarmSegmentSeconds}
            setPreAlarmSegmentSeconds={setPreAlarmSegmentSeconds}
            selectedServerId={selectedServerId}
            setSelectedServerId={setSelectedServerId}
            selectedCameraId={selectedCameraId}
            setSelectedCameraId={setSelectedCameraId}
            servers={serversTree}
            isLimitReached={isLimitReached}
            onAddCamera={onAddCamera}
            onRefreshLists={refreshLists}
            onSelectCamera={selectCameraForEdit}
            onMoveCamera={async (cameraToMove, serverId) => {
              const current = camerasMap[cameraToMove]
              if (!current) {
                return
              }
              const result = await dispatch(changeCameraParent({ ...current, serverId }))
              if (!changeCameraParent.rejected.match(result)) {
                await refreshLists()
              }
            }}
            onRemoveCamera={async (cameraToDelete) => {
              const result = await dispatch(removeCamera({ cameraId: cameraToDelete }))
              if (!removeCamera.rejected.match(result)) {
                setSelectedCameraId('')
                await refreshLists()
              }
            }}
          />
        )}

        {mainTab === 2 && <RetentionEventsTab />}

        <SimulationPanel cameras={Object.values(camerasMap)} />
        <LogJournalPanel />
      </Stack>
    </Box>
  )
}
