import { useRef } from 'react'
import { Navigate, Route, Routes, useLocation, useNavigate } from 'react-router-dom'
import { Box, Stack, Tab, Tabs } from '@mui/material'
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
import OnvifCameraSettingsPanel from './OnvifCameraSettingsPanel'

const TABS = ['cameras', 'mediamtx', 'monitors', 'storages', 'recording', 'onvif', 'onvif-settings'] as const

export default function AdminView() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const location = useLocation()
  const tab = TABS.indexOf(location.pathname.split('/').pop() as typeof TABS[number])
  const currentTab = tab === -1 ? 0 : tab

  const cameraControlRef = useRef<CameraControlHandle>(null)
  const recordingRef = useRef<RecordingProfilesHandle>(null)
  const mediaMtxRef = useRef<MediaMtxServersHandle>(null)

  const handleTabChange = async (_: unknown, v: number) => {
    if (currentTab === 0 && !(await cameraControlRef.current?.applyIfDirty() ?? true)) return
    if (currentTab === 1 && !(await mediaMtxRef.current?.applyIfDirty() ?? true)) return
    if (currentTab === 4 && !(await recordingRef.current?.applyIfDirty() ?? true)) return
    navigate(`/admin/${TABS[v]}`)
  }

  return (
    <Box sx={{ display: 'flex', height: '100%', overflow: 'hidden' }}>
      <Box sx={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        <Box sx={{ display: 'flex', justifyContent: 'flex-end', px: { xs: 2, md: 4 }, pt: 1 }}>
          <ViewControls target="operator" />
        </Box>
        <Box sx={{ flex: 1, minHeight: 0, overflow: 'hidden', px: { xs: 2, md: 4 }, pb: { xs: 2, md: 4 }, pt: 1, display: 'flex', flexDirection: 'column' }}>
          <Routes>
            <Route path="cameras" element={<CameraControlPanel ref={cameraControlRef} />} />
            <Route path="mediamtx" element={<Box sx={{ flex: 1, overflowY: 'auto' }}><MediaMtxServersPanel ref={mediaMtxRef} /></Box>} />
            <Route path="monitors" element={<Box sx={{ flex: 1, overflowY: 'auto' }}><MonitorListPanel /></Box>} />
            <Route path="storages" element={<Box sx={{ flex: 1, overflowY: 'auto' }}><StorageSetupPanel /></Box>} />
            <Route path="recording" element={<Box sx={{ flex: 1, overflowY: 'auto' }}><RecordingProfilesPanel ref={recordingRef} /></Box>} />
            <Route path="onvif" element={<OnvifSubscriptionsPanel />} />
            <Route path="onvif-settings" element={<Box sx={{ flex: 1, overflowY: 'auto' }}><OnvifCameraSettingsPanel /></Box>} />
            <Route path="*" element={<Navigate to="/admin/cameras" replace />} />
          </Routes>
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
        <Box sx={{ flex: 1, display: 'flex', flexDirection: 'column', justifyContent: 'flex-start' }}>
          <Tabs
            orientation="vertical"
            value={currentTab}
            onChange={(e, v) => void handleTabChange(e, v)}
            sx={{ '& .MuiTab-root': { minWidth: 90 } }}
          >
            <Tab label={t('nav.camSetup')} />
            <Tab label={t('nav.mediaMtx')} />
            <Tab label={t('nav.monitors')} />
            <Tab label={t('nav.storages')} />
            <Tab label={t('nav.recording')} />
            <Tab label={t('nav.onvif')} />
            <Tab label={t('nav.onvifSettings')} />
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
