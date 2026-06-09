import { useState } from 'react'
import { Box, Stack, Tab, Tabs } from '@mui/material'
import { useTranslation } from 'react-i18next'

import ViewControls from './ViewControls'
import MonitorsView from './MonitorsView'
import RetentionEventsTab from './RetentionEventsTab'
import SimulationFab from './SimulationFab'
import LogToggleButton from './LogToggleButton'

export default function OperatorView() {
  const { t } = useTranslation()
  const [tab, setTab] = useState(0)

  return (
    <Box sx={{ display: 'flex', height: '100%', overflow: 'hidden' }}>
      <Box sx={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        <Box sx={{ display: 'flex', justifyContent: 'flex-end', px: { xs: 2, md: 4 }, pt: 1 }}>
          <ViewControls target="admin" />
        </Box>
        <Box sx={{ flex: 1, minHeight: 0, overflow: 'hidden', px: { xs: 2, md: 4 }, pb: { xs: 2, md: 4 } }}>
          {tab === 0 && <MonitorsView readOnly />}
          {tab === 1 && (
            <Box sx={{ height: '100%', overflow: 'auto' }}>
              <RetentionEventsTab />
            </Box>
          )}
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
            value={tab}
            onChange={(_, v: number) => setTab(v)}
            sx={{ '& .MuiTab-root': { minWidth: 90 } }}
          >
            <Tab label={t('nav.monitors')} />
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
