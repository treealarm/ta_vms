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
import { useAppDispatch, useAppSelector } from '../store'
import { addStorage, loadStorages, removeStorage } from '../store/storageSlice'

export default function StorageSetupPanel() {
  const dispatch = useAppDispatch()
  const storages = useAppSelector((state) => state.storage.storages)
  const storeError = useAppSelector((state) => state.storage.status)

  const [storageId, setStorageId] = useState('')
  const [rootPath, setRootPath] = useState('')
  const [reservedGb, setReservedGb] = useState(0)
  const [localError, setLocalError] = useState('')

  useEffect(() => { void dispatch(loadStorages()) }, [dispatch])

  const onAdd = async () => {
    setLocalError('')
    if (!storageId.trim() || !rootPath.trim()) {
      setLocalError('Storage ID and root path are required')
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
      void dispatch(loadStorages())
    }
  }

  const onDelete = async (id: string) => {
    const result = await dispatch(removeStorage(id))
    if (!removeStorage.rejected.match(result)) {
      void dispatch(loadStorages())
    }
  }

  const error = localError || storeError

  return (
    <Card>
      <CardContent>
        <Stack spacing={2}>
          <Typography variant="subtitle1">Storages</Typography>

          <Stack direction={{ xs: 'column', sm: 'row' }} spacing={1.5} alignItems="flex-end">
            <TextField
              label="Storage ID"
              value={storageId}
              onChange={(e) => setStorageId(e.target.value)}
              size="small"
            />
            <TextField
              label="Root path"
              value={rootPath}
              onChange={(e) => setRootPath(e.target.value)}
              size="small"
              sx={{ flex: 1 }}
            />
            <TextField
              label="Reserved (GB)"
              type="number"
              value={reservedGb}
              onChange={(e) => setReservedGb(Number(e.target.value))}
              size="small"
              sx={{ width: 140 }}
            />
            <Button variant="outlined" onClick={() => void onAdd()}>
              Add
            </Button>
          </Stack>

          {error && <Typography color="error" variant="body2">{error}</Typography>}

          {storages.length === 0 ? (
            <Typography variant="body2" color="text.secondary">No storages configured</Typography>
          ) : (
            <Box sx={{ border: 1, borderColor: 'divider', borderRadius: 1 }}>
              {storages.map((s, i) => (
                <Stack
                  key={s.storageId}
                  direction="row"
                  alignItems="center"
                  spacing={2}
                  sx={{ px: 2, py: 1, borderTop: i > 0 ? 1 : 0, borderColor: 'divider' }}
                >
                  <Typography variant="body2" sx={{ fontWeight: 600, minWidth: 100 }}>
                    {s.storageId}
                  </Typography>
                  <Typography variant="body2" sx={{ flex: 1 }} color="text.secondary">
                    {s.rootPath}
                  </Typography>
                  <Typography variant="body2" color="text.secondary" sx={{ minWidth: 80, textAlign: 'right' }}>
                    {s.reservedBytes > 0
                      ? `${(s.reservedBytes / (1024 ** 3)).toFixed(1)} GB reserved`
                      : '—'}
                  </Typography>
                  <IconButton size="small" onClick={() => void onDelete(s.storageId)}>
                    <DeleteIcon fontSize="small" />
                  </IconButton>
                </Stack>
              ))}
            </Box>
          )}
        </Stack>
      </CardContent>
    </Card>
  )
}
