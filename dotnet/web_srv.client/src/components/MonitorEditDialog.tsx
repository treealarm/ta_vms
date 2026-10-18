import { useEffect, useState } from 'react'
import {
  Box,
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControl,
  InputLabel,
  MenuItem,
  Select,
  Stack,
  TextField,
} from '@mui/material'
import { useTranslation } from 'react-i18next'
import type { CameraSetupDto, MonitorDto, MonitorLayout } from '../dto/dtos'
import { MONITOR_LAYOUTS, layoutColumns, layoutSlotCount } from '../dto/dtos'

type Props = {
  open: boolean
  monitor: MonitorDto | null
  allCameras: CameraSetupDto[]
  onSave: (name: string, layout: MonitorLayout, slotCameras: string[]) => void
  onClose: () => void
}

export default function MonitorEditDialog({ open, monitor, allCameras, onSave, onClose }: Props) {
  const { t } = useTranslation()
  const [name, setName] = useState('')
  const [layout, setLayout] = useState<MonitorLayout>('2x2')
  const [slotCameras, setSlotCameras] = useState<string[]>([])

  useEffect(() => {
    if (!open) return
    const initialLayout = (monitor?.layout as MonitorLayout) ?? '2x2'
    setName(monitor?.name ?? '')
    setLayout(initialLayout)
    const count = layoutSlotCount(initialLayout)
    const initial: string[] = Array(count).fill('')
    for (const slot of monitor?.slots ?? []) {
      if (slot.slotIndex < count) initial[slot.slotIndex] = slot.cameraId
    }
    setSlotCameras(initial)
  }, [open, monitor])

  const handleLayoutChange = (newLayout: MonitorLayout) => {
    setLayout(newLayout)
    const count = layoutSlotCount(newLayout)
    setSlotCameras((prev) => Array.from({ length: count }, (_, i) => prev[i] ?? ''))
  }

  const setSlot = (index: number, cameraId: string) =>
    setSlotCameras((prev) => prev.map((v, i) => (i === index ? cameraId : v)))

  const handleSave = () => {
    if (!name.trim()) return
    onSave(name.trim(), layout, slotCameras)
  }

  const cols = layoutColumns(layout)

  return (
    <Dialog open={open} onClose={onClose} maxWidth="xs" fullWidth>
      <DialogTitle>{monitor ? t('monitors.editTitle') : t('monitors.newTitle')}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <TextField
            label={t('common.name')}
            value={name}
            onChange={(e) => setName(e.target.value)}
            size="small"
            fullWidth
            autoFocus
          />

          <FormControl size="small" fullWidth>
            <InputLabel>{t('monitors.layout')}</InputLabel>
            <Select
              value={layout}
              label={t('monitors.layout')}
              onChange={(e) => handleLayoutChange(e.target.value as MonitorLayout)}
            >
              {MONITOR_LAYOUTS.map((l) => (
                <MenuItem key={l} value={l}>{l}</MenuItem>
              ))}
            </Select>
          </FormControl>

          <Box
            sx={{
              display: 'grid',
              gridTemplateColumns: `repeat(${cols}, 1fr)`,
              gap: 1,
            }}
          >
            {slotCameras.map((cameraId, i) => (
              <FormControl key={i} size="small" fullWidth>
                <InputLabel>{i + 1}</InputLabel>
                <Select
                  value={cameraId}
                  label={String(i + 1)}
                  onChange={(e) => setSlot(i, e.target.value)}
                >
                  <MenuItem value=""><em>—</em></MenuItem>
                  {allCameras.map((c) => (
                    <MenuItem key={c.cameraId} value={c.cameraId}>
                      {c.name || c.cameraId}
                    </MenuItem>
                  ))}
                </Select>
              </FormControl>
            ))}
          </Box>
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>{t('common.cancel')}</Button>
        <Button onClick={handleSave} variant="contained" disabled={!name.trim()}>
          {t('common.save')}
        </Button>
      </DialogActions>
    </Dialog>
  )
}
