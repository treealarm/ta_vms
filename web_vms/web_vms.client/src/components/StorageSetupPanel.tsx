import { useEffect, useImperativeHandle, useRef, useState } from 'react'
import { Box, List, Stack, TextField, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { addStorage, loadStorages, removeStorage, updateStorage } from '../store/storageSlice'
import type { StorageDto } from '../dto/dtos'
import DetailToolbar from './settings/DetailToolbar'
import MasterDetailLayout from './settings/MasterDetailLayout'
import SettingsListItem from './settings/SettingsListItem'
import { useDirtyGuard } from './settings/useDirtyGuard'
import type { ApplyIfDirtyHandle } from './settings/useDirtyGuard'

const GIB = 1024 ** 3

const toGbString = (bytes: number) => bytes > 0 ? (bytes / GIB).toFixed(1) : '0'

type FormProps = {
  ref?: React.Ref<ApplyIfDirtyHandle>
  storage: StorageDto | null  // null = new
  onSaved: (storageId: string) => void
  onRemoved: () => void
}

function StorageForm({ ref, storage, onSaved, onRemoved }: FormProps) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const isNew = storage === null

  const [storageId, setStorageId] = useState(storage?.storageId ?? '')
  const [rootPath, setRootPath] = useState(storage?.rootPath ?? '')
  const [reservedGb, setReservedGb] = useState(toGbString(storage?.reservedBytes ?? 0))
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')

  // Snapshot for dirty detection and Cancel — only reservedGb is editable on existing storages
  const [snap, setSnap] = useState(toGbString(storage?.reservedBytes ?? 0))
  const dirty = !isNew && reservedGb !== snap

  const applyInternal = async (): Promise<boolean> => {
    if (isNew || !dirty) return true
    setSaving(true)
    setError('')
    const result = await dispatch(updateStorage({
      storageId: storage.storageId,
      rootPath: storage.rootPath,
      reservedBytes: Math.round(Number(reservedGb) * GIB),
    }))
    setSaving(false)
    if (updateStorage.rejected.match(result)) {
      setError(String(result.error?.message ?? t('storage.saveFailed')))
      return false
    }
    setSnap(reservedGb)
    await dispatch(loadStorages())
    return true
  }

  const handleApply = async (e: React.FormEvent) => {
    e.preventDefault()
    if (isNew) {
      setError('')
      if (!storageId.trim() || !rootPath.trim()) {
        setError(t('storage.required'))
        return
      }
      setSaving(true)
      const result = await dispatch(addStorage({
        storageId: storageId.trim(),
        rootPath: rootPath.trim(),
        reservedBytes: Math.round(Number(reservedGb) * GIB),
      }))
      setSaving(false)
      if (!addStorage.rejected.match(result)) {
        await dispatch(loadStorages())
        onSaved(storageId.trim())
      }
    } else {
      await applyInternal()
    }
  }

  const handleCancel = () => setReservedGb(snap)

  const handleRemove = async () => {
    const result = await dispatch(removeStorage(storage!.storageId))
    if (!removeStorage.rejected.match(result)) {
      await dispatch(loadStorages())
      onRemoved()
    }
  }

  useDirtyGuard(ref, dirty, applyInternal)

  return (
    <Box component="form" onSubmit={(e) => void handleApply(e)} sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
      <DetailToolbar
        title={isNew ? t('storage.newStorage') : storage.storageId}
        subtitle={isNew ? undefined : storage.rootPath}
        dirty={isNew ? Boolean(storageId.trim() && rootPath.trim()) : dirty}
        busy={saving}
        applyLabel={isNew ? t('common.add') : undefined}
        onCancel={isNew ? undefined : handleCancel}
        onDelete={isNew ? undefined : () => void handleRemove()}
        deleteLabel={t('storage.deleteStorage')}
      />

      <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', p: 2 }}>
        <Stack spacing={2} sx={{ maxWidth: 420 }}>
          <TextField
            label={t('storage.storageId')}
            value={storageId}
            onChange={(e) => setStorageId(e.target.value)}
            size="small"
            disabled={!isNew}
            required
          />
          <TextField
            label={t('storage.rootPath')}
            value={rootPath}
            onChange={(e) => setRootPath(e.target.value)}
            size="small"
            disabled={!isNew}
            required
          />
          <TextField
            label={t('storage.reservedGb')}
            type="number"
            value={reservedGb}
            onChange={(e) => setReservedGb(e.target.value)}
            size="small"
            sx={{ width: 140 }}
            inputProps={{ min: 0, step: 0.5 }}
          />
          {error && <Typography variant="caption" color="error">{error}</Typography>}
        </Stack>
      </Box>
    </Box>
  )
}

export default function StorageSetupPanel({ ref }: { ref?: React.Ref<ApplyIfDirtyHandle> }) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const storages = useAppSelector((state) => state.storage.storages)
  const storeError = useAppSelector((state) => state.storage.status)
  const [selection, setSelection] = useState<string | null>(null)  // storage id | 'new' | null

  const detailRef = useRef<ApplyIfDirtyHandle>(null)

  useEffect(() => {
    dispatch(loadStorages())
  }, [dispatch])

  const setSelectionSafe = async (sel: string | null) => {
    if (detailRef.current && !(await detailRef.current.applyIfDirty())) return
    setSelection(sel)
  }

  useImperativeHandle(ref, () => ({
    applyIfDirty: () => detailRef.current ? detailRef.current.applyIfDirty() : Promise.resolve(true)
  }))

  const selected = selection !== null && selection !== 'new'
    ? storages.find((s) => s.storageId === selection) ?? null
    : null

  const list = (
    <List dense disablePadding>
      {storages.map((s) => (
        <SettingsListItem
          key={s.storageId}
          selected={selection === s.storageId}
          onClick={() => void setSelectionSafe(s.storageId)}
          primary={s.storageId}
          secondary={s.rootPath}
        />
      ))}
      {storages.length === 0 && (
        <Typography variant="body2" color="text.secondary" sx={{ p: 1.5 }}>
          {t('storage.noStorages')}
        </Typography>
      )}
    </List>
  )

  return (
    <MasterDetailLayout
      listTitle={t('storage.title')}
      onAdd={() => void setSelectionSafe('new')}
      addTooltip={t('storage.newStorage')}
      list={list}
    >
      {selection === 'new' || selected ? (
        <StorageForm
          ref={detailRef}
          key={selection === 'new' ? 'new' : selected!.storageId}
          storage={selection === 'new' ? null : selected}
          onSaved={(storageId) => setSelection(storageId)}
          onRemoved={() => setSelection(null)}
        />
      ) : (
        <Box sx={{ p: 2 }}>
          <Typography color="text.secondary">{t('storage.selectHint')}</Typography>
          {storeError && <Typography color="error" variant="body2" sx={{ mt: 1 }}>{storeError}</Typography>}
        </Box>
      )}
    </MasterDetailLayout>
  )
}
