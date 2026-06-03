import { useState } from 'react'
import { Box, IconButton, Stack, Typography } from '@mui/material'
import KeyboardArrowUpIcon from '@mui/icons-material/KeyboardArrowUp'
import KeyboardArrowDownIcon from '@mui/icons-material/KeyboardArrowDown'
import ArticleIcon from '@mui/icons-material/Article'
import { useTranslation } from 'react-i18next'
import LogJournalPanel from './LogJournalPanel'

export default function LogDock() {
  const { t } = useTranslation()
  const [open, setOpen] = useState(false)

  return (
    <Box
      sx={{
        flexShrink: 0,
        borderTop: 1,
        borderColor: 'divider',
        display: 'flex',
        flexDirection: 'column',
        bgcolor: 'background.paper',
      }}
    >
      <Stack
        direction="row"
        alignItems="center"
        spacing={1}
        sx={{ px: 1.5, py: 0.5, cursor: 'pointer', userSelect: 'none' }}
        onClick={() => setOpen((v) => !v)}
      >
        <ArticleIcon fontSize="small" color="action" />
        <Typography variant="caption" fontWeight={600}>
          {t('logs.title')}
        </Typography>
        <Box flex={1} />
        <IconButton size="small">
          {open ? <KeyboardArrowDownIcon fontSize="small" /> : <KeyboardArrowUpIcon fontSize="small" />}
        </IconButton>
      </Stack>

      {open && (
        <Box sx={{ height: '40vh', overflow: 'auto', p: 1, borderTop: 1, borderColor: 'divider' }}>
          <LogJournalPanel />
        </Box>
      )}
    </Box>
  )
}
