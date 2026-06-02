import { useState } from 'react'
import { Box, Tab, Tabs } from '@mui/material'
import { useTranslation } from 'react-i18next'

import ViewControls from './ViewControls'
import MonitorsView from './MonitorsView'
import RetentionEventsTab from './RetentionEventsTab'
import SimulationFab from './SimulationFab'

export default function OperatorView() {
  const { t } = useTranslation()
  const [tab, setTab] = useState(0)

  return (
    <Box sx={{ display: 'flex', height: '100dvh', overflow: 'hidden' }}>
      <Box sx={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden', p: { xs: 2, md: 4 } }}>
        {tab === 0 && <MonitorsView readOnly />}
        {tab === 1 && (
          <Box sx={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
            <RetentionEventsTab />
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
        }}
      >
        <ViewControls target="admin" />

        <Box sx={{ flex: 1, display: 'flex', flexDirection: 'column', justifyContent: 'center' }}>
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

        <Box sx={{ pb: 1, display: 'flex', justifyContent: 'center' }}>
          <SimulationFab />
        </Box>
      </Box>
    </Box>
  )
}
