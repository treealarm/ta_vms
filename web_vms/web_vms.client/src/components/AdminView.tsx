import { useRef, useState } from 'react'
import { LangToggle } from '../App'
import {
  Box,
  Button,
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
import { useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import { useAppSelector } from '../store'

import MonitorListPanel from './MonitorListPanel'
import CameraControlPanel from './CameraControlPanel'
import type { CameraControlHandle } from './CameraControlPanel'
import StorageSetupPanel from './StorageSetupPanel'
import RecordingProfilesPanel from './RecordingProfilesPanel'
import type { RecordingProfilesHandle } from './RecordingProfilesPanel'
import MediaMtxServersPanel from './MediaMtxServersPanel'
import type { MediaMtxServersHandle } from './MediaMtxServersPanel'
import LogJournalPanel from './LogJournalPanel'
import SimulationPanel from './SimulationPanel'
import OnvifSubscriptionsPanel from './OnvifSubscriptionsPanel'

export default function AdminView() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const camerasMap = useAppSelector((state) => state.live.cameraStatesById)

  const [tab, setTab] = useState(0)
  const [testsOpen, setTestsOpen] = useState(false)

  const cameraControlRef = useRef<CameraControlHandle>(null)
  const recordingRef = useRef<RecordingProfilesHandle>(null)
  const mediaMtxRef = useRef<MediaMtxServersHandle>(null)

  const handleTabChange = async (_: unknown, v: number) => {
    if (tab === 0 && !(await cameraControlRef.current?.applyIfDirty() ?? true)) return
    if (tab === 1 && !(await mediaMtxRef.current?.applyIfDirty() ?? true)) return
    if (tab === 4 && !(await recordingRef.current?.applyIfDirty() ?? true)) return
    setTab(v)
  }

  return (
    <Box sx={{ display: 'flex', height: '100dvh', overflow: 'hidden' }}>
      <Box sx={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden', p: { xs: 2, md: 4 } }}>
        <Box sx={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
          {tab === 0 && <CameraControlPanel ref={cameraControlRef} />}
          {tab === 1 && <MediaMtxServersPanel ref={mediaMtxRef} />}
          {tab === 2 && <MonitorListPanel />}
          {tab === 3 && <StorageSetupPanel />}
          {tab === 4 && <RecordingProfilesPanel ref={recordingRef} />}
          {tab === 5 && <LogJournalPanel />}
          {tab === 6 && <OnvifSubscriptionsPanel />}
        </Box>
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
          value={tab}
          onChange={(e, v) => void handleTabChange(e, v)}
          sx={{ '& .MuiTab-root': { minWidth: 90 } }}
        >
          <Tab label={t('nav.camSetup')} />
          <Tab label={t('nav.mediaMtx')} />
          <Tab label={t('nav.monitors')} />
          <Tab label={t('nav.storages')} />
          <Tab label={t('nav.recording')} />
          <Tab label={t('nav.logs')} />
          <Tab label={t('nav.onvif')} />
        </Tabs>

        <Stack alignItems="center" spacing={0.5} sx={{ mb: 1 }}>
          <Tooltip title={t('tests.title')} placement="right">
            <IconButton
              onClick={() => setTestsOpen((v) => !v)}
              sx={{ color: testsOpen ? 'primary.main' : 'text.secondary' }}
            >
              <ScienceIcon />
            </IconButton>
          </Tooltip>
          <Button
            size="small"
            variant="text"
            onClick={() => navigate('/operator')}
            sx={{ minWidth: 0, px: 1, py: 0.5, fontSize: '0.7rem', color: 'text.secondary' }}
          >
            {t('nav.goOperator')}
          </Button>
          <LangToggle />
        </Stack>
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
