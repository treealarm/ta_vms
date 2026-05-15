import { useEffect, useState } from 'react'
import {
  Box,
  IconButton,
  Paper,
  Stack,
  Tab,
  Tabs,
  Tooltip,
  Typography,
} from '@mui/material'
import ScienceIcon from '@mui/icons-material/Science'
import CloseIcon from '@mui/icons-material/Close'

import { loadCameras, loadServers } from './store/liveSlice'
import { loadMonitors } from './store/monitorsSlice'
import { loadMediaMtxServers } from './store/mediaMtxSlice'
import { useAppDispatch, useAppSelector } from './store'
import CameraControlPanel from './components/CameraControlPanel'
import LogJournalPanel from './components/LogJournalPanel'
import SimulationPanel from './components/SimulationPanel'
import RetentionEventsTab from './components/RetentionEventsTab'
import StorageSetupPanel from './components/StorageSetupPanel'
import RecordingProfilesPanel from './components/RecordingProfilesPanel'
import MediaMtxServersPanel from './components/MediaMtxServersPanel'
import MonitorsView from './components/MonitorsView'

export default function App() {
  const dispatch = useAppDispatch()
  const [mainTab, setMainTab] = useState(0)
  const [testsOpen, setTestsOpen] = useState(false)

  const camerasMap = useAppSelector((state) => state.live.cameraStatesById)

  useEffect(() => {
    let cancelled = false
    let retryTimer: ReturnType<typeof setTimeout> | null = null

    const bootstrap = async (attempt: number) => {
      if (cancelled) return
      const [camerasResult, serversResult, monitorsResult, mtxResult] = await Promise.all([
        dispatch(loadCameras()),
        dispatch(loadServers()),
        dispatch(loadMonitors()),
        dispatch(loadMediaMtxServers()),
      ])
      const hasError =
        loadCameras.rejected.match(camerasResult) ||
        loadServers.rejected.match(serversResult) ||
        loadMonitors.rejected.match(monitorsResult) ||
        loadMediaMtxServers.rejected.match(mtxResult)
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
        {mainTab === 0 && <MonitorsView />}

        {mainTab !== 0 && (
          <Box sx={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
            {mainTab === 1 && <CameraControlPanel />}
            {mainTab === 2 && <RetentionEventsTab />}
            {mainTab === 3 && <StorageSetupPanel />}
            {mainTab === 4 && <RecordingProfilesPanel />}
            {mainTab === 5 && <MediaMtxServersPanel />}
            {mainTab === 6 && <LogJournalPanel />}
          </Box>
        )}
      </Box>

      <Box
        sx={{
          order: -1,
          borderRight: 1,
          borderColor: 'divider',
          display: 'flex',
          flexDirection: 'column',
          justifyContent: 'space-between',
        }}
      >
        <Tabs
          orientation="vertical"
          value={mainTab}
          onChange={(_, v: number) => setMainTab(v)}
          sx={{ '& .MuiTab-root': { minWidth: 90 } }}
        >
          <Tab label="Monitors" />
          <Tab label="Cam setup" />
          <Tab label="Events" />
          <Tab label="Storages" />
          <Tab label="Recording" />
          <Tab label="MediaMTX" />
          <Tab label="Logs" />
        </Tabs>

        <Tooltip title="Tests" placement="right">
          <IconButton
            onClick={() => setTestsOpen((v) => !v)}
            sx={{ m: 1, color: testsOpen ? 'primary.main' : 'text.secondary' }}
          >
            <ScienceIcon />
          </IconButton>
        </Tooltip>
      </Box>

      {testsOpen && (
        <Paper
          elevation={8}
          sx={{
            position: 'fixed',
            bottom: 16,
            left: 108,
            width: 460,
            maxHeight: '80vh',
            zIndex: 1300,
            display: 'flex',
            flexDirection: 'column',
            overflow: 'hidden',
          }}
        >
          <Stack
            direction="row"
            alignItems="center"
            justifyContent="space-between"
            sx={{ px: 2, py: 1, borderBottom: 1, borderColor: 'divider', flexShrink: 0 }}
          >
            <Typography variant="subtitle1" fontWeight={600}>Tests</Typography>
            <IconButton size="small" onClick={() => setTestsOpen(false)}>
              <CloseIcon fontSize="small" />
            </IconButton>
          </Stack>
          <Box sx={{ overflow: 'auto', p: 2 }}>
            <SimulationPanel cameras={Object.values(camerasMap)} />
          </Box>
        </Paper>
      )}
    </Box>
  )
}
