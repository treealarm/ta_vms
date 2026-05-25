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

type Props = {
  cameraId: string
}

export default function CameraStoragesControl({ cameraId }: Props) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const allStorages = useAppSelector((state) => state.storage.storages)
  const savedAssignments = useAppSelector((state) => state.storage.cameraAssignments[cameraId] ?? [])

  const [assignments, setAssignments] = useState<CameraStorageAssignmentDto[]>(savedAssignments)
  const [selectedStorageId, setSelectedStorageId] = useState('')
  const [dirty, setDirty] = useState(false)

  useEffect(() => { void dispatch(loadStorages()) }, [dispatch])
  useEffect(() => {
    void dispatch(loadCameraStorages(cameraId))
  }, [dispatch, cameraId])

  useEffect(() => {
    setAssignments(savedAssignments)
    setDirty(false)
  }, [savedAssignments])

  // Auto-apply on unmount if dirty
  const applyRef = useRef(async () => {})
  applyRef.current = async () => {
    if (!dirty) return
    await dispatch(setCameraStorages({ cameraId, assignments }))
  }
  useEffect(() => () => { void applyRef.current() }, [])

  // Warn browser on close when dirty
  useEffect(() => {
    if (!dirty) return
    const handler = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = '' }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [dirty])

  const assignedIds = new Set(assignments.map((a) => a.storageId))
  const available = allStorages.filter((s) => !assignedIds.has(s.storageId))

  const rebuildPriorities = (list: CameraStorageAssignmentDto[]) =>
    list.map((a, i) => ({ ...a, priority: i }))

  const onAdd = () => {
    if (!selectedStorageId) return
    setAssignments(rebuildPriorities([
      ...assignments,
      { cameraId, storageId: selectedStorageId, priority: assignments.length }
    ]))
    setSelectedStorageId('')
    setDirty(true)
  }

  const onRemove = (storageId: string) => {
    setAssignments(rebuildPriorities(assignments.filter((a) => a.storageId !== storageId)))
    setDirty(true)
  }

  const move = (index: number, direction: -1 | 1) => {
    const next = [...assignments]
    const target = index + direction
    if (target < 0 || target >= next.length) return
    ;[next[index], next[target]] = [next[target], next[index]]
    setAssignments(rebuildPriorities(next))
    setDirty(true)
  }

  const onApply = async () => {
    await dispatch(setCameraStorages({ cameraId, assignments }))
    setDirty(false)
  }

  const onCancel = () => {
    setAssignments(savedAssignments)
    setDirty(false)
  }

  if (!cameraId) return null

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
        <Button variant="text" size="small" disabled={!dirty} onClick={onCancel}>
          {t('common.cancel')}
        </Button>
        <Button variant="contained" size="small" disabled={!dirty} onClick={() => void onApply()}>
          {t('common.apply')}
        </Button>
      </Stack>
    </Stack>
  )
}
