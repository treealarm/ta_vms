import { useRef, useState } from 'react'
import { Box, Tab, Tabs } from '@mui/material'
import { useTranslation } from 'react-i18next'
import ViewControls from './ViewControls'

import MonitorListPanel from './MonitorListPanel'
import CameraControlPanel from './CameraControlPanel'
import type { CameraControlHandle } from './CameraControlPanel'
import StorageSetupPanel from './StorageSetupPanel'
import RecordingProfilesPanel from './RecordingProfilesPanel'
import type { RecordingProfilesHandle } from './RecordingProfilesPanel'
import MediaMtxServersPanel from './MediaMtxServersPanel'
import type { MediaMtxServersHandle } from './MediaMtxServersPanel'
import SimulationFab from './SimulationFab'
import LogToggleButton from './LogToggleButton'
import OnvifSubscriptionsPanel from './OnvifSubscriptionsPanel'

export default function AdminView() {
  const { t } = useTranslation()

  const [tab, setTab] = useState(0)

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
    <Box sx={{ display: 'flex', height: '100%', overflow: 'hidden' }}>
      <Box sx={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden', p: { xs: 2, md: 4 } }}>
        <Box sx={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
          {tab === 0 && <CameraControlPanel ref={cameraControlRef} />}
          {tab === 1 && <MediaMtxServersPanel ref={mediaMtxRef} />}
          {tab === 2 && <MonitorListPanel />}
          {tab === 3 && <StorageSetupPanel />}
          {tab === 4 && <RecordingProfilesPanel ref={recordingRef} />}
          {tab === 5 && <OnvifSubscriptionsPanel />}
        </Box>
      </Box>

      <Box
        sx={{
          order: -1,
          borderRight: 1,
          borderColor: 'divider',
          display: 'flex',
          flexDirection: 'column',
        }}
      >
        <ViewControls target="operator" />

        <Box sx={{ flex: 1, display: 'flex', flexDirection: 'column', justifyContent: 'center' }}>
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
            <Tab label={t('nav.onvif')} />
          </Tabs>
        </Box>

        <Stack alignItems="center" sx={{ pb: 1 }}>
          <LogToggleButton />
          <SimulationFab />
        </Stack>
      </Box>
    </Box>
  )
}
