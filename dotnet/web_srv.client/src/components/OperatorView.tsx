import { Navigate, Route, Routes, useLocation, useNavigate } from 'react-router-dom'
import { Box, Stack } from '@mui/material'

import ViewControls from './ViewControls'
import GroupedNav from './GroupedNav'
import { OPERATOR_NAV_GROUPS } from './navGroups'
import MonitorsView from './MonitorsView'
import EventsJournal from './events/EventsJournal'
import ArchiveGapsTab from './ArchiveGapsTab'
import SimulationFab from './SimulationFab'
import LogToggleButton from './LogToggleButton'
import ForensicSearchTab from './ForensicSearchTab'
import MapsOperatorPanel from './maps/MapsOperatorPanel'
import ControlPanel from './control/ControlPanel'

export default function OperatorView() {
  const navigate = useNavigate()
  const location = useLocation()
  const current = location.pathname.split('/').pop() ?? ''

  return (
    <Box sx={{ display: 'flex', height: '100%', overflow: 'hidden' }}>
      <Box sx={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        <Box sx={{ display: 'flex', justifyContent: 'flex-end', px: { xs: 2, md: 4 }, pt: 1 }}>
          <ViewControls current="operator" />
        </Box>
        <Box sx={{ flex: 1, minHeight: 0, overflow: 'hidden', display: 'flex', flexDirection: 'column', px: { xs: 2, md: 4 }, pt: 1, pb: { xs: 2, md: 4 } }}>
          <Routes>
            <Route path="monitors" element={<MonitorsView readOnly />} />
            <Route path="investigations" element={<MonitorsView readOnly kind="investigation" />} />
            <Route
              path="map"
              element={(
                <Box sx={{ height: '100%', minHeight: 0, display: 'flex', flexDirection: 'column' }}>
                  <MapsOperatorPanel />
                </Box>
              )}
            />
            <Route
              path="control"
              element={(
                <Box sx={{ height: '100%', minHeight: 0, display: 'flex', flexDirection: 'column' }}>
                  <ControlPanel />
                </Box>
              )}
            />
            <Route
              path="events"
              element={(
                <Box sx={{ height: '100%', overflow: 'auto' }}>
                  <EventsJournal />
                </Box>
              )}
            />
            <Route
              path="gaps"
              element={(
                <Box sx={{ height: '100%', overflow: 'auto' }}>
                  <ArchiveGapsTab />
                </Box>
              )}
            />
            <Route
              path="object-findings"
              element={(
                <Box sx={{ height: '100%', minHeight: 0, overflow: 'hidden' }}>
                  <ForensicSearchTab />
                </Box>
              )}
            />
            <Route path="forensic" element={<Navigate to="/operator/object-findings" replace />} />
            <Route path="*" element={<Navigate to="/operator/monitors" replace />} />
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
          groups={OPERATOR_NAV_GROUPS}
          current={current}
          onSelect={(slug) => navigate(`/operator/${slug}`)}
        />

        <Stack alignItems="center" sx={{ pb: 1, pt: 1 }}>
          <LogToggleButton />
          <SimulationFab />
        </Stack>
      </Box>
    </Box>
  )
}
