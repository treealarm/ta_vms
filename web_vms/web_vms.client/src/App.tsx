import { useEffect, useMemo, useState } from 'react'
import {
  Alert,
  Box,
  Card,
  CardContent,
  Stack,
  Tab,
  Tabs,
} from '@mui/material'

import { loadCameras, loadServers } from './store/liveSlice'
import { useAppDispatch, useAppSelector } from './store'
import CameraUnifiedCard from './components/CameraUnifiedCard'
import CameraControlPanel from './components/CameraControlPanel'
import LogJournalPanel from './components/LogJournalPanel'
import SimulationPanel from './components/SimulationPanel'
import RetentionEventsTab from './components/RetentionEventsTab'
import StorageSetupPanel from './components/StorageSetupPanel'

const MAX_CAMERAS = 4

export default function App() {
  const dispatch = useAppDispatch()
  const [mainTab, setMainTab] = useState(0)
  const [soloCameraId, setSoloCameraId] = useState<string | null>(null)

  const status = useAppSelector((state) =>
    state.live.status !== 'Ready' ? state.live.status : state.fs.status
  )
  const camerasMap = useAppSelector((state) => state.live.cameraStatesById)

  const { visibleCameras } = useMemo(() => {
    const arr = Object.values(camerasMap)
    return { visibleCameras: arr.slice(0, MAX_CAMERAS) }
  }, [camerasMap])

  const gridCameras = useMemo(() => {
    if (soloCameraId == null) return visibleCameras
    return visibleCameras.filter((c) => c.cameraId === soloCameraId)
  }, [soloCameraId, visibleCameras])

  const monitorGridSx = useMemo(() => {
    const n = gridCameras.length
    if (n <= 0) return { flex: 1, minHeight: 0, width: '100%' }
    const base = {
      display: 'grid' as const,
      gap: 2,
      flex: 1,
      minHeight: 0,
      width: '100%',
      height: '100%',
      alignContent: 'stretch' as const
    }
    if (n === 1) return { ...base, gridTemplateColumns: 'minmax(0, 1fr)', gridTemplateRows: 'minmax(0, 1fr)' }
    if (n === 2) return { ...base, gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr)', gridTemplateRows: 'minmax(0, 1fr)' }
    return { ...base, gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr)', gridTemplateRows: 'minmax(0, 1fr) minmax(0, 1fr)' }
  }, [gridCameras.length])

  useEffect(() => {
    if (mainTab !== 0) setSoloCameraId(null)
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
      if (cancelled) return
      const [camerasResult, serversResult] = await Promise.all([
        dispatch(loadCameras()),
        dispatch(loadServers()),
      ])
      const hasError =
        loadCameras.rejected.match(camerasResult) ||
        loadServers.rejected.match(serversResult)
      if (hasError && attempt < 20 && !cancelled) {
        retryTimer = setTimeout(() => { void bootstrap(attempt + 1) }, 1500)
      }
    }

    void bootstrap(0)
    return () => {
      cancelled = true
      if (retryTimer != null) clearTimeout(retryTimer)
    }
  }, [dispatch])

  return (
    <Box sx={{ display: 'flex', height: '100dvh', overflow: 'hidden' }}>
      <Box
        sx={{
          flex: 1,
          minWidth: 0,
          display: 'flex',
          flexDirection: 'column',
          overflow: 'hidden',
          p: { xs: 2, md: 4 }
        }}
      >
        {mainTab === 0 && (
          <Stack spacing={2} sx={{ flex: 1, minHeight: 0 }}>
            <Card sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
              <CardContent
                sx={{
                  flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column',
                  overflow: 'hidden', pt: 2, '&:last-child': { pb: 2 }
                }}
              >
                <Box sx={monitorGridSx}>
                  {gridCameras.map((camera) => (
                    <Box key={camera.cameraId} sx={{ minWidth: 0, minHeight: 0, display: 'flex', overflow: 'hidden' }}>
                      <CameraUnifiedCard
                        camera={camera}
                        gridSoloActive={soloCameraId === camera.cameraId}
                        onToggleGridSolo={
                          visibleCameras.length > 1
                            ? () => setSoloCameraId((id) => (id === camera.cameraId ? null : camera.cameraId))
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

        {mainTab !== 0 && (
          <Box sx={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
            {mainTab === 1 && <CameraControlPanel />}
            {mainTab === 2 && <RetentionEventsTab />}
            {mainTab === 3 && <StorageSetupPanel />}
            {mainTab === 4 && <SimulationPanel cameras={Object.values(camerasMap)} />}
            {mainTab === 5 && <LogJournalPanel />}
          </Box>
        )}
      </Box>

      <Tabs
        orientation="vertical"
        value={mainTab}
        onChange={(_, v: number) => setMainTab(v)}
        sx={{
          order: -1,
          borderRight: 1,
          borderColor: 'divider',
          '& .MuiTab-root': { minWidth: 90 }
        }}
      >
        <Tab label="Monitor" />
        <Tab label="Cam setup" />
        <Tab label="Events" />
        <Tab label="Storages" />
        <Tab label="Tests" />
        <Tab label="Logs" />
      </Tabs>
    </Box>
  )
}
