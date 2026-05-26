import { useState } from 'react'
import { Box, Button, Stack, Tab, Tabs } from '@mui/material'
import { useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'

import { LangToggle } from '../App'
import MonitorsView from './MonitorsView'
import RetentionEventsTab from './RetentionEventsTab'

export default function OperatorView() {
  const { t } = useTranslation()
  const navigate = useNavigate()
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
          justifyContent: 'space-between',
        }}
      >
        <Tabs
          orientation="vertical"
          value={tab}
          onChange={(_, v: number) => setTab(v)}
          sx={{ '& .MuiTab-root': { minWidth: 90 } }}
        >
          <Tab label={t('nav.monitors')} />
          <Tab label={t('nav.events')} />
        </Tabs>

        <Stack alignItems="center" spacing={0.5} sx={{ mb: 1 }}>
          <Button
            size="small"
            variant="text"
            onClick={() => navigate('/admin')}
            sx={{ minWidth: 0, px: 1, py: 0.5, fontSize: '0.7rem', color: 'text.secondary' }}
          >
            {t('nav.goAdmin')}
          </Button>
          <LangToggle />
        </Stack>
      </Box>
    </Box>
  )
}
