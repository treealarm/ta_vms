import { useEffect, useRef, useState } from 'react'
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
import { useTranslation } from 'react-i18next'

import { loadCameras, loadServers } from './store/liveSlice'
import { loadMonitors } from './store/monitorsSlice'
import { loadMediaMtxServers } from './store/mediaMtxSlice'
import { useAppDispatch, useAppSelector } from './store'
import CameraControlPanel from './components/CameraControlPanel'
import type { CameraControlHandle } from './components/CameraControlPanel'
import LogJournalPanel from './components/LogJournalPanel'
import SimulationPanel from './components/SimulationPanel'
import RetentionEventsTab from './components/RetentionEventsTab'
import StorageSetupPanel from './components/StorageSetupPanel'
import RecordingProfilesPanel from './components/RecordingProfilesPanel'
import type { RecordingProfilesHandle } from './components/RecordingProfilesPanel'
import MediaMtxServersPanel from './components/MediaMtxServersPanel'
import type { MediaMtxServersHandle } from './components/MediaMtxServersPanel'
import MonitorsView from './components/MonitorsView'

export default function App() {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const [mainTab, setMainTab] = useState(0)
  const [testsOpen, setTestsOpen] = useState(false)

  const camerasMap = useAppSelector((state) => state.live.cameraStatesById)

  const cameraControlRef = useRef<CameraControlHandle>(null)
  const recordingRef = useRef<RecordingProfilesHandle>(null)
  const mediaMtxRef = useRef<MediaMtxServersHandle>(null)

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

  // Auto-apply unsaved changes before switching tabs
  const handleTabChange = async (_: unknown, v: number) => {
    if (mainTab === 1) await cameraControlRef.current?.applyIfDirty()
    if (mainTab === 4) await recordingRef.current?.applyIfDirty()
    if (mainTab === 5) await mediaMtxRef.current?.applyIfDirty()
    setMainTab(v)
  }

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
            {mainTab === 1 && <CameraControlPanel ref={cameraControlRef} />}
            {mainTab === 2 && <RetentionEventsTab />}
            {mainTab === 3 && <StorageSetupPanel />}
            {mainTab === 4 && <RecordingProfilesPanel ref={recordingRef} />}
            {mainTab === 5 && <MediaMtxServersPanel ref={mediaMtxRef} />}
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
          onChange={(e, v) => void handleTabChange(e, v)}
          sx={{ '& .MuiTab-root': { minWidth: 90 } }}
        >
          <Tab label={t('nav.monitors')} />
          <Tab label={t('nav.camSetup')} />
          <Tab label={t('nav.events')} />
          <Tab label={t('nav.storages')} />
          <Tab label={t('nav.recording')} />
          <Tab label={t('nav.mediaMtx')} />
          <Tab label={t('nav.logs')} />
        </Tabs>

        <Tooltip title={t('tests.title')} placement="right">
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
            <Typography variant="subtitle1" fontWeight={600}>{t('tests.title')}</Typography>
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
