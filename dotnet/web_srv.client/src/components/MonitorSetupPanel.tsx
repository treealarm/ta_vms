import { useState } from 'react'
import { Box, FormControl, InputLabel, MenuItem, Select, Stack, TextField } from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { removeMonitor, setMonitorCameras, setMonitorSlot, upsertMonitor } from '../store/monitorsSlice'
import type { MonitorDto, MonitorLayout } from '../dto/dtos'
import { MONITOR_LAYOUTS, layoutColumns, layoutSlotCount } from '../dto/dtos'
import DetailToolbar from './settings/DetailToolbar'
import { useDirtyGuard } from './settings/useDirtyGuard'
import type { ApplyIfDirtyHandle } from './settings/useDirtyGuard'

type Props = {
  ref?: React.Ref<ApplyIfDirtyHandle>
  monitor: MonitorDto | null  // null = new
  onSaved: (monitorId: string) => void
  onRemoved: () => void
}

const initialSlots = (monitor: MonitorDto | null, layout: MonitorLayout): string[] => {
  const count = layoutSlotCount(layout)
  const slots: string[] = Array(count).fill('')
  for (const slot of monitor?.slots ?? []) {
    if (slot.slotIndex < count) slots[slot.slotIndex] = slot.cameraId
  }
  return slots
}

export default function MonitorSetupPanel({ ref, monitor, onSaved, onRemoved }: Props) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const allCameras = useAppSelector((s) => Object.values(s.live.cameraStatesById))
  const isNew = monitor === null

  const [name, setName] = useState(monitor?.name ?? '')
  const [layout, setLayout] = useState<MonitorLayout>((monitor?.layout as MonitorLayout) ?? '2x2')
  const [slotCameras, setSlotCameras] = useState<string[]>(() => initialSlots(monitor, (monitor?.layout as MonitorLayout) ?? '2x2'))

  // Snapshot of the saved state — used for dirty detection and Cancel
  const [snap, setSnap] = useState({
    name: monitor?.name ?? '',
    layout: (monitor?.layout as MonitorLayout) ?? '2x2',
    slotCameras: initialSlots(monitor, (monitor?.layout as MonitorLayout) ?? '2x2'),
  })

  const slotsChanged =
    slotCameras.length !== snap.slotCameras.length
    || slotCameras.some((v, i) => v !== snap.slotCameras[i])

  const dirty = !isNew && (
    name !== snap.name
    || layout !== snap.layout
    || slotsChanged
  )

  const handleLayoutChange = (newLayout: MonitorLayout) => {
    setLayout(newLayout)
    const count = layoutSlotCount(newLayout)
    setSlotCameras((prev) => Array.from({ length: count }, (_, i) => prev[i] ?? ''))
  }

  const setSlot = (index: number, cameraId: string) =>
    setSlotCameras((prev) => prev.map((v, i) => (i === index ? cameraId : v)))

  // Save flow: upsert the monitor shell, then sync the camera pool and slot
  // assignments (the API has no single "save whole monitor" call).
  const applyInternal = async (): Promise<boolean> => {
    if (!isNew && !dirty) return true
    if (!name.trim()) return false
    const result = await dispatch(upsertMonitor({
      monitorId: monitor?.monitorId ?? '',
      name: name.trim(),
      layout,
      cameraIds: monitor?.cameraIds ?? [],
      slots: monitor?.slots ?? [],
      kind: 'wall',
      owner: '',
    }))
    if (!upsertMonitor.fulfilled.match(result)) return false

    const savedId = result.payload.monitorId
    const poolIds = [...new Set(slotCameras.filter(Boolean))]
    await dispatch(setMonitorCameras({ monitorId: savedId, cameraIds: poolIds }))
    for (let i = 0; i < slotCameras.length; i++) {
      if (slotCameras[i]) {
        await dispatch(setMonitorSlot({ monitorId: savedId, slotIndex: i, cameraId: slotCameras[i] }))
      }
    }
    setSnap({ name: name.trim(), layout, slotCameras: [...slotCameras] })
    onSaved(savedId)
    return true
  }

  const handleApply = async (e: React.FormEvent) => {
    e.preventDefault()
    await applyInternal()
  }

  const handleCancel = () => {
    setName(snap.name)
    setLayout(snap.layout)
    setSlotCameras([...snap.slotCameras])
  }

  const handleRemove = async () => {
    await dispatch(removeMonitor(monitor!.monitorId))
    onRemoved()
  }

  // For a new form "dirty" means "complete enough to save" — navigation then
  // auto-creates it instead of silently discarding the entered data
  useDirtyGuard(ref, isNew ? Boolean(name.trim()) : dirty, applyInternal)

  const cols = layoutColumns(layout)

  return (
    <Box component="form" onSubmit={(e) => void handleApply(e)} sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
      <DetailToolbar
        title={isNew ? t('monitors.newTitle') : monitor.name}
        subtitle={isNew ? undefined : monitor.monitorId}
        dirty={isNew ? Boolean(name.trim()) : dirty}
        applyLabel={isNew ? t('common.add') : undefined}
        onCancel={isNew ? undefined : handleCancel}
        onDelete={isNew ? undefined : () => void handleRemove()}
        deleteLabel={t('monitors.deleteMonitor')}
      />

      <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', p: 2 }}>
        <Stack spacing={2} sx={{ maxWidth: 420 }}>
          <TextField
            label={t('common.name')}
            value={name}
            onChange={(e) => setName(e.target.value)}
            size="small"
            required
            autoFocus={isNew}
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
      </Box>
    </Box>
  )
}
