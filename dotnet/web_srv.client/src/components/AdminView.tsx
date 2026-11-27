import { useRef } from 'react'
import { Navigate, Route, Routes, useLocation, useNavigate } from 'react-router-dom'
import { Box, Stack } from '@mui/material'
import { useRegisterPendingEdits } from './settings/pendingEditsContext'
import ViewControls from './ViewControls'
import GroupedNav from './GroupedNav'
import { ADMIN_NAV_GROUPS } from './navGroups'

import MonitorListPanel from './MonitorListPanel'
import CameraControlPanel from './CameraControlPanel'
import StorageSetupPanel from './StorageSetupPanel'
import RecordingProfilesPanel from './RecordingProfilesPanel'
import RecordingSchedulesPanel from './RecordingSchedulesPanel'
import MediaMtxServersPanel from './MediaMtxServersPanel'
import DeploymentPanel from './DeploymentPanel'
import SimulationFab from './SimulationFab'
import LogToggleButton from './LogToggleButton'
import OnvifSubscriptionsPanel from './OnvifSubscriptionsPanel'
import OnvifCameraSettingsPanel from './OnvifCameraSettingsPanel'
import EdgeArchivePanel from './EdgeArchivePanel'
import AnalyticsWatchesPanel from './AnalyticsWatchesPanel'
import MapsAdminPanel from './maps/MapsAdminPanel'
import NodesAdminPanel from './nodes/NodesAdminPanel'
import EventSubscriptionsPanel from './EventSubscriptionsPanel'
import type { ApplyIfDirtyHandle } from './settings/useDirtyGuard'

export default function AdminView() {
  const navigate = useNavigate()
  const location = useLocation()
  // Route segment of the open tab; an unknown one matches no group and the splat route redirects.
  const current = location.pathname.split('/').pop() ?? ''

  // Only one route is mounted at a time, so a single ref serves every panel
  const panelRef = useRef<ApplyIfDirtyHandle>(null)
  // Serializes tab switches so a rapid second click cannot start a concurrent
  // applyIfDirty save for the same dirty form
  const switching = useRef(false)

  // Also reachable from outside: leaving for the operator view is routing, not a tab change.
  useRegisterPendingEdits(panelRef)

  const selectTab = async (slug: string) => {
    if (switching.current) return
    switching.current = true
    try {
      if (!(await panelRef.current?.applyIfDirty() ?? true)) return
      navigate(`/admin/${slug}`)
    } finally {
      switching.current = false
    }
  }

  return (
    <Box sx={{ display: 'flex', height: '100%', overflow: 'hidden' }}>
      <Box sx={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        <Box sx={{ display: 'flex', justifyContent: 'flex-end', px: { xs: 2, md: 4 }, pt: 1 }}>
          <ViewControls current="admin" />
        </Box>
        <Box sx={{ flex: 1, minHeight: 0, overflow: 'hidden', px: { xs: 2, md: 4 }, pb: { xs: 2, md: 4 }, pt: 1, display: 'flex', flexDirection: 'column' }}>
          <Routes>
            <Route path="cameras" element={<CameraControlPanel ref={panelRef} />} />
            <Route path="maps" element={<MapsAdminPanel ref={panelRef} />} />
            <Route path="nodes" element={<NodesAdminPanel ref={panelRef} />} />
            <Route path="mediamtx" element={<MediaMtxServersPanel ref={panelRef} />} />
            <Route path="monitors" element={<MonitorListPanel ref={panelRef} />} />
            <Route path="storages" element={<StorageSetupPanel ref={panelRef} />} />
            <Route path="recording" element={<RecordingProfilesPanel ref={panelRef} />} />
            <Route path="schedules" element={<RecordingSchedulesPanel ref={panelRef} />} />
            <Route path="onvif" element={<OnvifSubscriptionsPanel ref={panelRef} />} />
            <Route path="onvif-settings" element={<OnvifCameraSettingsPanel ref={panelRef} />} />
            <Route path="event-routing" element={<EventSubscriptionsPanel ref={panelRef} />} />
            <Route path="analytics" element={<AnalyticsWatchesPanel ref={panelRef} />} />
            {/* No guard: every action on the edge archive is immediate and already confirmed, so
                the tab holds no unsaved edit to lose. */}
            <Route path="edge-archive" element={<EdgeArchivePanel />} />
            <Route path="deployment" element={<DeploymentPanel ref={panelRef} />} />
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
          maxWidth: 240,
        }}
      >
        <GroupedNav
          groups={ADMIN_NAV_GROUPS}
          current={current}
          onSelect={(slug) => void selectTab(slug)}
        />

        <Stack alignItems="center" sx={{ pb: 1, pt: 1 }}>
          <LogToggleButton />
          <SimulationFab />
        </Stack>
      </Box>
    </Box>
  )
}
