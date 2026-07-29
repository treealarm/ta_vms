import { Navigate, Route, Routes, useLocation, useNavigate } from 'react-router-dom'
import { Box, Stack, Tab, Tabs } from '@mui/material'
import { useTranslation } from 'react-i18next'

import ViewControls from './ViewControls'
import MonitorsView from './MonitorsView'
import RetentionEventsTab from './RetentionEventsTab'
import SimulationFab from './SimulationFab'
import LogToggleButton from './LogToggleButton'

const TABS = ['monitors', 'investigations', 'events'] as const

export default function OperatorView() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const location = useLocation()
  const tab = TABS.indexOf(location.pathname.split('/').pop() as typeof TABS[number])

  return (
    <Box sx={{ display: 'flex', height: '100%', overflow: 'hidden' }}>
      <Box sx={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        <Box sx={{ display: 'flex', justifyContent: 'flex-end', px: { xs: 2, md: 4 }, pt: 1 }}>
          <ViewControls target="admin" />
        </Box>
        <Box sx={{ flex: 1, minHeight: 0, overflow: 'hidden', display: 'flex', flexDirection: 'column', px: { xs: 2, md: 4 }, pt: 1, pb: { xs: 2, md: 4 } }}>
          <Routes>
            <Route path="monitors" element={<MonitorsView readOnly />} />
            <Route path="investigations" element={<MonitorsView readOnly kind="investigation" />} />
            <Route
              path="events"
              element={(
                <Box sx={{ height: '100%', overflow: 'auto' }}>
                  <RetentionEventsTab />
                </Box>
              )}
            />
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
        }}
      >
        <Box sx={{ flex: 1, display: 'flex', flexDirection: 'column', justifyContent: 'flex-start' }}>
          <Tabs
            orientation="vertical"
            value={tab === -1 ? 0 : tab}
            onChange={(_, v: number) => navigate(`/operator/${TABS[v]}`)}
            sx={{ '& .MuiTab-root': { minWidth: 90 } }}
          >
            <Tab label={t('nav.monitors')} />
            <Tab label={t('nav.investigations')} />
            <Tab label={t('nav.events')} />
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
