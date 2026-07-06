import { useRef } from 'react'
import { Navigate, Route, Routes, useLocation, useNavigate } from 'react-router-dom'
import { Box, Stack, Tab, Tabs } from '@mui/material'
import { useTranslation } from 'react-i18next'
import ViewControls from './ViewControls'

import MonitorListPanel from './MonitorListPanel'
import CameraControlPanel from './CameraControlPanel'
import StorageSetupPanel from './StorageSetupPanel'
import RecordingProfilesPanel from './RecordingProfilesPanel'
import MediaMtxServersPanel from './MediaMtxServersPanel'
import SimulationFab from './SimulationFab'
import LogToggleButton from './LogToggleButton'
import OnvifSubscriptionsPanel from './OnvifSubscriptionsPanel'
import OnvifCameraSettingsPanel from './OnvifCameraSettingsPanel'
import AnalyticsWatchesPanel from './AnalyticsWatchesPanel'
import type { ApplyIfDirtyHandle } from './settings/useDirtyGuard'

const TABS = ['cameras', 'mediamtx', 'monitors', 'storages', 'recording', 'onvif', 'onvif-settings', 'analytics'] as const

export default function AdminView() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const location = useLocation()
  const tab = TABS.indexOf(location.pathname.split('/').pop() as typeof TABS[number])
  const currentTab = tab === -1 ? 0 : tab

  // Only one route is mounted at a time, so a single ref serves every panel
  const panelRef = useRef<ApplyIfDirtyHandle>(null)
  // Serializes tab switches so a rapid second click cannot start a concurrent
  // applyIfDirty save for the same dirty form
  const switching = useRef(false)

  const handleTabChange = async (_: unknown, v: number) => {
    if (switching.current) return
    switching.current = true
    try {
      if (!(await panelRef.current?.applyIfDirty() ?? true)) return
      navigate(`/admin/${TABS[v]}`)
    } finally {
      switching.current = false
    }
  }

  return (
    <Box sx={{ display: 'flex', height: '100%', overflow: 'hidden' }}>
      <Box sx={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        <Box sx={{ display: 'flex', justifyContent: 'flex-end', px: { xs: 2, md: 4 }, pt: 1 }}>
          <ViewControls target="operator" />
        </Box>
        <Box sx={{ flex: 1, minHeight: 0, overflow: 'hidden', px: { xs: 2, md: 4 }, pb: { xs: 2, md: 4 }, pt: 1, display: 'flex', flexDirection: 'column' }}>
          <Routes>
            <Route path="cameras" element={<CameraControlPanel ref={panelRef} />} />
            <Route path="mediamtx" element={<MediaMtxServersPanel ref={panelRef} />} />
            <Route path="monitors" element={<MonitorListPanel ref={panelRef} />} />
            <Route path="storages" element={<StorageSetupPanel ref={panelRef} />} />
            <Route path="recording" element={<RecordingProfilesPanel ref={panelRef} />} />
            <Route path="onvif" element={<OnvifSubscriptionsPanel ref={panelRef} />} />
            <Route path="onvif-settings" element={<OnvifCameraSettingsPanel />} />
            <Route path="analytics" element={<AnalyticsWatchesPanel ref={panelRef} />} />
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
            <Tab label={t('nav.analytics')} />
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
