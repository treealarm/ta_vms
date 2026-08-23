import { useMemo, useState } from 'react'
import { Box, IconButton, Paper, Stack, Tooltip, Typography } from '@mui/material'
import ScienceIcon from '@mui/icons-material/Science'
import CloseIcon from '@mui/icons-material/Close'
import { useTranslation } from 'react-i18next'
import { useAppSelector } from '../store'
import SimulationPanel from './SimulationPanel'

export default function SimulationFab() {
  const { t } = useTranslation()
  const [open, setOpen] = useState(false)
  // See MonitorsView: deriving the list inside the selector returns a new array every call.
  const camerasById = useAppSelector((s) => s.live.cameraStatesById)
  const cameras = useMemo(() => Object.values(camerasById), [camerasById])

  return (
    <>
      <Tooltip title={t('tests.title')} placement="right">
        <IconButton
          onClick={() => setOpen((v) => !v)}
          sx={{ color: open ? 'primary.main' : 'text.secondary' }}
        >
          <ScienceIcon />
        </IconButton>
      </Tooltip>

      {open && (
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
            <IconButton size="small" onClick={() => setOpen(false)}>
              <CloseIcon fontSize="small" />
            </IconButton>
          </Stack>
          <Box sx={{ overflow: 'auto', p: 2 }}>
            <SimulationPanel cameras={cameras} />
          </Box>
        </Paper>
      )}
    </>
  )
}
