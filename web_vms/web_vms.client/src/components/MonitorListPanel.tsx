import { useState } from 'react'
import {
  Box,
  Button,
  Divider,
  IconButton,
  List,
  ListItem,
  ListItemText,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import EditIcon from '@mui/icons-material/Edit'
import DeleteIcon from '@mui/icons-material/Delete'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { removeMonitor, setMonitorCameras, upsertMonitor } from '../store/monitorsSlice'
import type { MonitorLayout } from '../dto/dtos'
import MonitorEditDialog from './MonitorEditDialog'

export default function MonitorListPanel() {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const monitors = useAppSelector((s) => s.monitors.monitors)
  const allCameras = useAppSelector((s) => Object.values(s.live.cameraStatesById))

  const [dialogOpen, setDialogOpen] = useState(false)
  const [editingId, setEditingId] = useState<string | null>(null)

  const editingMonitor = editingId ? (monitors.find((m) => m.monitorId === editingId) ?? null) : null

  const openCreate = () => { setEditingId(null); setDialogOpen(true) }
  const openEdit = (id: string) => { setEditingId(id); setDialogOpen(true) }
  const closeDialog = () => setDialogOpen(false)

  const handleSave = async (name: string, layout: MonitorLayout, cameraIds: string[]) => {
    setDialogOpen(false)
    const existing = editingId ? monitors.find((m) => m.monitorId === editingId) : null
    const result = await dispatch(upsertMonitor({
      monitorId: existing?.monitorId ?? '',
      name,
      layout,
      cameraIds: existing?.cameraIds ?? [],
      slots: existing?.slots ?? [],
    }))
    if (upsertMonitor.fulfilled.match(result)) {
      const savedId = result.payload.monitorId
      if (cameraIds.join(',') !== (existing?.cameraIds ?? []).join(',')) {
        await dispatch(setMonitorCameras({ monitorId: savedId, cameraIds }))
      }
    }
  }

  const handleDelete = (id: string) => void dispatch(removeMonitor(id))

  return (
    <Stack spacing={2} sx={{ maxWidth: 560 }}>
      <Stack direction="row" alignItems="center" justifyContent="space-between">
        <Typography variant="h6">{t('monitors.title')}</Typography>
        <Tooltip title={t('monitors.newMonitor')}>
          <IconButton size="small" onClick={openCreate}>
            <AddIcon />
          </IconButton>
        </Tooltip>
      </Stack>

      <List disablePadding>
        {monitors.map((m, i) => (
          <Box key={m.monitorId}>
            {i > 0 && <Divider />}
            <ListItem
              disablePadding
              secondaryAction={
                <Stack direction="row" spacing={0.5}>
                  <IconButton size="small" onClick={() => openEdit(m.monitorId)}>
                    <EditIcon fontSize="small" />
                  </IconButton>
                  <Tooltip title={t('monitors.deleteMonitor')}>
                    <IconButton size="small" color="error" onClick={() => handleDelete(m.monitorId)}>
                      <DeleteIcon fontSize="small" />
                    </IconButton>
                  </Tooltip>
                </Stack>
              }
            >
              <ListItemText
                sx={{ py: 0.5, pl: 1 }}
                primary={m.name}
                secondary={`${m.layout} · ${m.cameraIds.length} ${t('monitors.cameras', { count: m.cameraIds.length })}`}
              />
            </ListItem>
          </Box>
        ))}
        {monitors.length === 0 && (
          <Typography variant="body2" color="text.secondary" sx={{ pl: 1, py: 1 }}>
            {t('monitors.noMonitors')}
          </Typography>
        )}
      </List>

      {monitors.length === 0 && (
        <Box>
          <Button variant="outlined" size="small" onClick={openCreate}>
            {t('monitors.newMonitor')}
          </Button>
        </Box>
      )}

      <MonitorEditDialog
        open={dialogOpen}
        monitor={editingMonitor}
        allCameras={allCameras}
        onSave={(name, layout, cameraIds) => void handleSave(name, layout, cameraIds)}
        onClose={closeDialog}
      />
    </Stack>
  )
}
