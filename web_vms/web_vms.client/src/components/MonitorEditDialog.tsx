import { useEffect, useState } from 'react'
import {
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControl,
  IconButton,
  InputLabel,
  MenuItem,
  Select,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import DeleteIcon from '@mui/icons-material/Delete'
import ArrowUpwardIcon from '@mui/icons-material/ArrowUpward'
import ArrowDownwardIcon from '@mui/icons-material/ArrowDownward'
import { useTranslation } from 'react-i18next'
import type { CameraSetupDto, MonitorDto, MonitorLayout } from '../dto/dtos'
import { MONITOR_LAYOUTS } from '../dto/dtos'

type Props = {
  open: boolean
  monitor: MonitorDto | null
  allCameras: CameraSetupDto[]
  onSave: (name: string, layout: MonitorLayout, cameraIds: string[]) => void
  onClose: () => void
}

export default function MonitorEditDialog({ open, monitor, allCameras, onSave, onClose }: Props) {
  const { t } = useTranslation()
  const [name, setName] = useState('')
  const [layout, setLayout] = useState<MonitorLayout>('2x2')
  const [cameraIds, setCameraIds] = useState<string[]>([])
  const [addCameraId, setAddCameraId] = useState('')

  useEffect(() => {
    if (open) {
      setName(monitor?.name ?? '')
      setLayout((monitor?.layout as MonitorLayout) ?? '2x2')
      setCameraIds(monitor?.cameraIds ? [...monitor.cameraIds] : [])
      setAddCameraId('')
    }
  }, [open, monitor])

  const availableToAdd = allCameras.filter((c) => !cameraIds.includes(c.cameraId))

  const handleAdd = () => {
    if (!addCameraId || cameraIds.includes(addCameraId)) return
    setCameraIds((ids) => [...ids, addCameraId])
    setAddCameraId('')
  }

  const handleRemove = (id: string) => setCameraIds((ids) => ids.filter((x) => x !== id))

  const moveUp = (idx: number) => {
    if (idx === 0) return
    setCameraIds((ids) => {
      const next = [...ids]
      ;[next[idx - 1], next[idx]] = [next[idx], next[idx - 1]]
      return next
    })
  }

  const moveDown = (idx: number) => {
    setCameraIds((ids) => {
      if (idx >= ids.length - 1) return ids
      const next = [...ids]
      ;[next[idx], next[idx + 1]] = [next[idx + 1], next[idx]]
      return next
    })
  }

  const handleSave = () => {
    if (!name.trim()) return
    onSave(name.trim(), layout, cameraIds)
  }

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
              onChange={(e) => setLayout(e.target.value as MonitorLayout)}
            >
              {MONITOR_LAYOUTS.map((l) => (
                <MenuItem key={l} value={l}>{l}</MenuItem>
              ))}
            </Select>
          </FormControl>

          <Typography variant="subtitle2" color="text.secondary">
            {t('monitors.cameraPool')}
          </Typography>

          {cameraIds.map((id, idx) => (
            <Stack key={id} direction="row" alignItems="center" spacing={0.5}>
              <Typography variant="body2" sx={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis' }}>
                {allCameras.find((c) => c.cameraId === id)?.name || id}
              </Typography>
              <IconButton size="small" onClick={() => moveUp(idx)} disabled={idx === 0}>
                <ArrowUpwardIcon fontSize="small" />
              </IconButton>
              <IconButton size="small" onClick={() => moveDown(idx)} disabled={idx === cameraIds.length - 1}>
                <ArrowDownwardIcon fontSize="small" />
              </IconButton>
              <IconButton size="small" onClick={() => handleRemove(id)}>
                <DeleteIcon fontSize="small" />
              </IconButton>
            </Stack>
          ))}

          {availableToAdd.length > 0 && (
            <Stack direction="row" spacing={1} alignItems="center">
              <FormControl size="small" sx={{ flex: 1 }}>
                <InputLabel>{t('monitors.addCamera')}</InputLabel>
                <Select
                  value={addCameraId}
                  label={t('monitors.addCamera')}
                  onChange={(e) => setAddCameraId(e.target.value)}
                >
                  {availableToAdd.map((c) => (
                    <MenuItem key={c.cameraId} value={c.cameraId}>{c.name || c.cameraId}</MenuItem>
                  ))}
                </Select>
              </FormControl>
              <IconButton onClick={handleAdd} disabled={!addCameraId} color="primary">
                <AddIcon />
              </IconButton>
            </Stack>
          )}
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
