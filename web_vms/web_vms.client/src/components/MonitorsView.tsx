import { useEffect, useState } from 'react'
import {
  Alert,
  Box,
  Card,
  CardContent,
  IconButton,
  Stack,
  Tab,
  Tabs,
  Tooltip,
  Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import EditIcon from '@mui/icons-material/Edit'
import DeleteIcon from '@mui/icons-material/Delete'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import {
  loadMonitors,
  removeMonitor,
  setMonitorCameras,
  upsertMonitor,
} from '../store/monitorsSlice'
import type { MonitorLayout } from '../dto/dtos'
import MonitorGrid from './MonitorGrid'
import MonitorEditDialog from './MonitorEditDialog'

export default function MonitorsView() {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const monitors = useAppSelector((state) => state.monitors.monitors)
  const status = useAppSelector((state) => state.monitors.status)
  const allCameras = useAppSelector((state) => Object.values(state.live.cameraStatesById))

  const [selectedMonitorId, setSelectedMonitorId] = useState<string | null>(null)
  const [dialogOpen, setDialogOpen] = useState(false)
  const [editingMonitorId, setEditingMonitorId] = useState<string | null>(null)

  useEffect(() => {
    void dispatch(loadMonitors())
  }, [dispatch])

  // Auto-select first monitor when list loads or changes
  useEffect(() => {
    if (monitors.length > 0 && (selectedMonitorId === null || !monitors.some((m) => m.monitorId === selectedMonitorId))) {
      setSelectedMonitorId(monitors[0].monitorId)
    } else if (monitors.length === 0) {
      setSelectedMonitorId(null)
    }
  }, [monitors, selectedMonitorId])

  const selectedMonitor = monitors.find((m) => m.monitorId === selectedMonitorId) ?? null
  const editingMonitor = editingMonitorId ? (monitors.find((m) => m.monitorId === editingMonitorId) ?? null) : null

  const handleOpenCreate = () => {
    setEditingMonitorId(null)
    setDialogOpen(true)
  }

  const handleOpenEdit = (monitorId: string) => {
    setEditingMonitorId(monitorId)
    setDialogOpen(true)
  }

  const handleDialogSave = async (name: string, layout: MonitorLayout, cameraIds: string[]) => {
    setDialogOpen(false)
    const existing = editingMonitorId ? monitors.find((m) => m.monitorId === editingMonitorId) : null
    const result = await dispatch(
      upsertMonitor({
        monitorId: existing?.monitorId ?? '',
        name,
        layout,
        cameraIds: existing?.cameraIds ?? [],
        slots: existing?.slots ?? [],
      })
    )

    if (upsertMonitor.fulfilled.match(result)) {
      const savedId = result.payload.monitorId
      setSelectedMonitorId(savedId)
      if (cameraIds.join(',') !== (existing?.cameraIds ?? []).join(',')) {
        await dispatch(setMonitorCameras({ monitorId: savedId, cameraIds }))
      }
    }
  }

  const handleDelete = async (monitorId: string) => {
    await dispatch(removeMonitor(monitorId))
  }

  return (
    <Stack spacing={2} sx={{ flex: 1, minHeight: 0 }}>
      <Card sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        <CardContent
          sx={{
            flex: 1,
            minHeight: 0,
            display: 'flex',
            flexDirection: 'column',
            overflow: 'hidden',
            pt: 1,
            '&:last-child': { pb: 2 },
          }}
        >
          {/* Monitor tabs bar */}
          <Box sx={{ display: 'flex', alignItems: 'center', borderBottom: 1, borderColor: 'divider', mb: 2, flexShrink: 0 }}>
            <Tabs
              value={selectedMonitorId ?? false}
              onChange={(_, v: string) => setSelectedMonitorId(v)}
              variant="scrollable"
              scrollButtons="auto"
              sx={{ flex: 1, minWidth: 0 }}
            >
              {monitors.map((m) => (
                <Tab key={m.monitorId} value={m.monitorId} label={m.name} />
              ))}
            </Tabs>

            <Tooltip title={t('monitors.newMonitor')}>
              <IconButton size="small" onClick={handleOpenCreate} sx={{ mx: 1 }}>
                <AddIcon />
              </IconButton>
            </Tooltip>

            {selectedMonitor && (
              <>
                <Tooltip title={t('monitors.editMonitor')}>
                  <IconButton size="small" onClick={() => handleOpenEdit(selectedMonitor.monitorId)}>
                    <EditIcon fontSize="small" />
                  </IconButton>
                </Tooltip>
                <Tooltip title={t('monitors.deleteMonitor')}>
                  <IconButton size="small" onClick={() => void handleDelete(selectedMonitor.monitorId)} color="error">
                    <DeleteIcon fontSize="small" />
                  </IconButton>
                </Tooltip>
              </>
            )}
          </Box>

          {/* Monitor grid */}
          {selectedMonitor ? (
            <MonitorGrid monitor={selectedMonitor} />
          ) : (
            <Box sx={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
              <Stack alignItems="center" spacing={1}>
                <Typography color="text.secondary">{t('monitors.noMonitors')}</Typography>
                <Typography variant="body2" color="text.disabled">
                  {t('monitors.createHint')}
                </Typography>
              </Stack>
            </Box>
          )}
        </CardContent>
      </Card>

      {status !== 'Ready' && (
        <Alert severity={status.startsWith('Error') ? 'error' : 'info'} sx={{ flexShrink: 0 }}>
          {status}
        </Alert>
      )}

      <MonitorEditDialog
        open={dialogOpen}
        monitor={editingMonitor}
        allCameras={allCameras}
        onSave={(name, layout, cameraIds) => void handleDialogSave(name, layout, cameraIds)}
        onClose={() => setDialogOpen(false)}
      />
    </Stack>
  )
}
