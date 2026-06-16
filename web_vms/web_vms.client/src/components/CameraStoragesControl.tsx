import { useEffect, useRef, useState } from 'react'
import {
  Box,
  Button,
  FormControl,
  IconButton,
  InputLabel,
  MenuItem,
  Select,
  Stack,
  Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'
import ArrowUpwardIcon from '@mui/icons-material/ArrowUpward'
import ArrowDownwardIcon from '@mui/icons-material/ArrowDownward'
import DeleteIcon from '@mui/icons-material/Delete'
import { useAppDispatch, useAppSelector } from '../store'
import {
  loadCameraStorages,
  loadStorages,
  setCameraStorages,
} from '../store/storageSlice'
import type { CameraStorageAssignmentDto } from '../dto/dtos'

type Props =
  | { cameraId: string; value?: undefined; onChange?: undefined }
  | { cameraId?: undefined; value: CameraStorageAssignmentDto[]; onChange: (v: CameraStorageAssignmentDto[]) => void }

export default function CameraStoragesControl({ cameraId, value, onChange }: Props) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const allStorages = useAppSelector((state) => state.storage.storages)
  const savedAssignments = useAppSelector((state) => state.storage.cameraAssignments[cameraId ?? ''] ?? [])

  const controlled = cameraId === undefined

  const [assignments, setAssignments] = useState<CameraStorageAssignmentDto[]>(
    controlled ? value : savedAssignments
  )
  const [selectedStorageId, setSelectedStorageId] = useState('')
  const [dirty, setDirty] = useState(false)

  useEffect(() => { void dispatch(loadStorages()) }, [dispatch])

  useEffect(() => {
    if (!controlled) void dispatch(loadCameraStorages(cameraId))
  }, [dispatch, cameraId, controlled])

  useEffect(() => {
    if (!controlled) {
      setAssignments(savedAssignments)
      setDirty(false)
    }
  }, [savedAssignments, controlled])

  useEffect(() => {
    if (controlled) setAssignments(value)
  }, [value, controlled])

  // Auto-apply on unmount if dirty (uncontrolled mode only)
  const applyRef = useRef(async () => {})
  applyRef.current = async () => {
    if (controlled || !dirty) return
    await dispatch(setCameraStorages({ cameraId: cameraId!, assignments }))
  }
  useEffect(() => () => { void applyRef.current() }, [])

  // Warn browser on close when dirty (uncontrolled mode only)
  useEffect(() => {
    if (controlled || !dirty) return
    const handler = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = '' }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [dirty, controlled])

  const assignedIds = new Set(assignments.map((a) => a.storageId))
  const available = allStorages.filter((s) => !assignedIds.has(s.storageId))

  const rebuildPriorities = (list: CameraStorageAssignmentDto[]) =>
    list.map((a, i) => ({ ...a, priority: i }))

  const update = (next: CameraStorageAssignmentDto[]) => {
    setAssignments(next)
    if (controlled) {
      onChange(next)
    } else {
      setDirty(true)
    }
  }

  const onAdd = () => {
    if (!selectedStorageId) return
    update(rebuildPriorities([
      ...assignments,
      { cameraId: cameraId ?? '', storageId: selectedStorageId, priority: assignments.length }
    ]))
    setSelectedStorageId('')
  }

  const onRemove = (storageId: string) => {
    update(rebuildPriorities(assignments.filter((a) => a.storageId !== storageId)))
  }

  const move = (index: number, direction: -1 | 1) => {
    const next = [...assignments]
    const target = index + direction
    if (target < 0 || target >= next.length) return
    ;[next[index], next[target]] = [next[target], next[index]]
    update(rebuildPriorities(next))
  }

  const onApply = async () => {
    await dispatch(setCameraStorages({ cameraId: cameraId!, assignments }))
    setDirty(false)
  }

  const onCancel = () => {
    setAssignments(savedAssignments)
    setDirty(false)
  }

  return (
    <Stack spacing={1}>
      <Typography variant="body2" color="text.secondary">{t('storageControl.title')}</Typography>

      {assignments.length === 0 ? (
        <Typography variant="body2" color="text.secondary" sx={{ pl: 1 }}>
          {t('storageControl.noStorages')}
        </Typography>
      ) : (
        <Box sx={{ border: 1, borderColor: 'divider', borderRadius: 1 }}>
          {assignments.map((a, i) => (
            <Stack
              key={a.storageId}
              direction="row"
              alignItems="center"
              spacing={1}
              sx={{ px: 1.5, py: 0.5, borderTop: i > 0 ? 1 : 0, borderColor: 'divider' }}
            >
              <Typography variant="body2" color="text.secondary" sx={{ minWidth: 24 }}>
                {i + 1}
              </Typography>
              <Typography variant="body2" sx={{ flex: 1 }}>{a.storageId}</Typography>
              <IconButton size="small" onClick={() => move(i, -1)} disabled={i === 0}>
                <ArrowUpwardIcon fontSize="inherit" />
              </IconButton>
              <IconButton size="small" onClick={() => move(i, 1)} disabled={i === assignments.length - 1}>
                <ArrowDownwardIcon fontSize="inherit" />
              </IconButton>
              <IconButton size="small" onClick={() => onRemove(a.storageId)}>
                <DeleteIcon fontSize="inherit" />
              </IconButton>
            </Stack>
          ))}
        </Box>
      )}

      <Stack direction="row" spacing={1} alignItems="center">
        <FormControl size="small" sx={{ flex: 1 }} disabled={available.length === 0}>
          <InputLabel>{t('storageControl.addStorage')}</InputLabel>
          <Select
            label={t('storageControl.addStorage')}
            value={selectedStorageId}
            onChange={(e) => setSelectedStorageId(e.target.value)}
          >
            {available.map((s) => (
              <MenuItem key={s.storageId} value={s.storageId}>{s.storageId}</MenuItem>
            ))}
          </Select>
        </FormControl>
        <Button variant="outlined" size="small" disabled={!selectedStorageId} onClick={onAdd}>
          {t('common.add')}
        </Button>
        {!controlled && (
          <>
            <Button variant="text" size="small" disabled={!dirty} onClick={onCancel}>
              {t('common.cancel')}
            </Button>
            <Button variant="contained" size="small" disabled={!dirty} onClick={() => void onApply()}>
              {t('common.apply')}
            </Button>
          </>
        )}
      </Stack>
    </Stack>
  )
}
