import { useEffect, useState } from 'react'
import {
  Box,
  Button,
  Card,
  CardContent,
  IconButton,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import DeleteIcon from '@mui/icons-material/Delete'
import EditIcon from '@mui/icons-material/Edit'
import CheckIcon from '@mui/icons-material/Check'
import CloseIcon from '@mui/icons-material/Close'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { addStorage, loadStorages, removeStorage, updateStorage } from '../store/storageSlice'
import type { StorageDto } from '../dto/dtos'

interface EditState {
  reservedGb: string
}

function StorageRow({ s, onDeleted }: { s: StorageDto; onDeleted: () => void }) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const [editing, setEditing] = useState(false)
  const [form, setForm] = useState<EditState>({ rootPath: '', reservedGb: '0' })
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')

  const startEdit = () => {
    setForm({
      reservedGb: s.reservedBytes > 0 ? (s.reservedBytes / (1024 ** 3)).toFixed(1) : '0',
    })
    setError('')
    setEditing(true)
  }

  const cancelEdit = () => { setEditing(false); setError('') }

  const save = async () => {
    setSaving(true)
    const result = await dispatch(updateStorage({
      storageId: s.storageId,
      rootPath: s.rootPath,
      reservedBytes: Math.round(Number(form.reservedGb) * 1024 * 1024 * 1024),
    }))
    setSaving(false)
    if (updateStorage.rejected.match(result)) {
      setError(String(result.error?.message ?? t('storage.saveFailed')))
      return
    }
    setEditing(false)
    onDeleted() // reuse to trigger reload
  }

  if (editing) {
    return (
      <Stack spacing={0.5} sx={{ px: 2, py: 1, borderTop: '1px solid', borderColor: 'divider' }}>
        <Stack direction="row" spacing={1} alignItems="center">
          <Typography variant="body2" sx={{ fontWeight: 600, minWidth: 100 }}>{s.storageId}</Typography>
          <Typography variant="body2" sx={{ flex: 1 }} color="text.secondary">{s.rootPath}</Typography>
          <TextField
            size="small"
            label={t('storage.reservedGb')}
            type="number"
            value={form.reservedGb}
            onChange={(e) => setForm((f) => ({ ...f, reservedGb: e.target.value }))}
            sx={{ width: 120 }}
            inputProps={{ min: 0, step: 0.5 }}
          />
          <IconButton size="small" onClick={() => void save()} disabled={saving} color="primary">
            <CheckIcon fontSize="small" />
          </IconButton>
          <IconButton size="small" onClick={cancelEdit} disabled={saving}>
            <CloseIcon fontSize="small" />
          </IconButton>
        </Stack>
        {error && <Typography variant="caption" color="error">{error}</Typography>}
      </Stack>
    )
  }

  return (
    <Stack
      direction="row"
      alignItems="center"
      spacing={2}
      sx={{ px: 2, py: 1, borderTop: '1px solid', borderColor: 'divider' }}
    >
      <Typography variant="body2" sx={{ fontWeight: 600, minWidth: 100 }}>{s.storageId}</Typography>
      <Typography variant="body2" sx={{ flex: 1 }} color="text.secondary">{s.rootPath}</Typography>
      <Typography variant="body2" color="text.secondary" sx={{ minWidth: 80, textAlign: 'right' }}>
        {s.reservedBytes > 0
          ? t('storage.gbReserved', { n: (s.reservedBytes / (1024 ** 3)).toFixed(1) })
          : '—'}
      </Typography>
      <IconButton size="small" onClick={startEdit}>
        <EditIcon fontSize="small" />
      </IconButton>
      <IconButton size="small" onClick={() => void dispatch(removeStorage(s.storageId)).then(onDeleted)}>
        <DeleteIcon fontSize="small" />
      </IconButton>
    </Stack>
  )
}

export default function StorageSetupPanel() {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const storages = useAppSelector((state) => state.storage.storages)
  const storeError = useAppSelector((state) => state.storage.status)

  const [storageId, setStorageId] = useState('')
  const [rootPath, setRootPath] = useState('')
  const [reservedGb, setReservedGb] = useState(0)
  const [localError, setLocalError] = useState('')

  const reload = () => void dispatch(loadStorages())

  useEffect(reload, [dispatch])

  const onAdd = async () => {
    setLocalError('')
    if (!storageId.trim() || !rootPath.trim()) {
      setLocalError(t('storage.required'))
      return
    }
    const result = await dispatch(addStorage({
      storageId: storageId.trim(),
      rootPath: rootPath.trim(),
      reservedBytes: reservedGb * 1024 * 1024 * 1024
    }))
    if (!addStorage.rejected.match(result)) {
      setStorageId('')
      setRootPath('')
      setReservedGb(0)
      reload()
    }
  }

  const error = localError || storeError

  return (
    <Card>
      <CardContent>
        <Stack spacing={2}>
          <Typography variant="subtitle1">{t('storage.title')}</Typography>

          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5} alignItems="flex-end">
            <TextField
              label={t('storage.storageId')}
              value={storageId}
              onChange={(e) => setStorageId(e.target.value)}
              size="small"
            />
            <TextField
              label={t('storage.rootPath')}
              value={rootPath}
              onChange={(e) => setRootPath(e.target.value)}
              size="small"
              sx={{ flex: 1 }}
            />
            <TextField
              label={t('storage.reservedGb')}
              type="number"
              value={reservedGb}
              onChange={(e) => setReservedGb(Number(e.target.value))}
              size="small"
              sx={{ width: 140 }}
              inputProps={{ min: 0, step: 0.5 }}
            />
            <Button variant="outlined" onClick={() => void onAdd()}>
              {t('common.add')}
            </Button>
          </Stack>

          {error && <Typography color="error" variant="body2">{error}</Typography>}

          {storages.length === 0 ? (
            <Typography variant="body2" color="text.secondary">{t('storage.noStorages')}</Typography>
          ) : (
            <Box sx={{ border: 1, borderColor: 'divider', borderRadius: 1, '& > *:first-of-type': { borderTop: 0 } }}>
              {storages.map((s) => (
                <StorageRow key={s.storageId} s={s} onDeleted={reload} />
              ))}
            </Box>
          )}
        </Stack>
      </CardContent>
    </Card>
  )
}
