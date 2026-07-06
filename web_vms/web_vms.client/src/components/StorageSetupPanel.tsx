import { useEffect, useState } from 'react'
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
import { useMasterDetailSelection } from './settings/useMasterDetailSelection'

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

  // A cleared/invalid field must block the save — Number('') is 0 and would
  // silently wipe the reservation on the auto-apply navigation path
  const parseReservedBytes = (): number | null => {
    if (reservedGb.trim() === '') return null
    const gb = Number(reservedGb)
    if (Number.isNaN(gb) || gb < 0) return null
    return Math.round(gb * GIB)
  }

  const applyInternal = async (): Promise<boolean> => {
    if (!isNew && !dirty) return true
    setError('')
    const reservedBytes = parseReservedBytes()
    if (reservedBytes === null) {
      setError(t('storage.invalidReserved'))
      return false
    }
    if (isNew) {
      if (!storageId.trim() || !rootPath.trim()) {
        setError(t('storage.required'))
        return false
      }
      setSaving(true)
      const result = await dispatch(addStorage({
        storageId: storageId.trim(),
        rootPath: rootPath.trim(),
        reservedBytes,
      }))
      setSaving(false)
      if (addStorage.rejected.match(result)) {
        setError(String(result.error?.message ?? t('storage.saveFailed')))
        return false
      }
      await dispatch(loadStorages())
      onSaved(storageId.trim())
      return true
    }
    setSaving(true)
    const result = await dispatch(updateStorage({
      storageId: storage.storageId,
      rootPath: storage.rootPath,
      reservedBytes,
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
    await applyInternal()
  }

  const handleCancel = () => {
    setError('')
    setReservedGb(snap)
  }

  const handleRemove = async () => {
    setError('')
    const result = await dispatch(removeStorage(storage!.storageId))
    if (removeStorage.rejected.match(result)) {
      setError(String(result.error?.message ?? t('storage.saveFailed')))
      return
    }
    await dispatch(loadStorages())
    onRemoved()
  }

  // For a new form "dirty" means "complete enough to save" — navigation then
  // auto-creates it instead of silently discarding the entered data
  useDirtyGuard(ref, isNew ? Boolean(storageId.trim() && rootPath.trim()) : dirty, applyInternal)

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

  const { isNew, selected, detailRef, detailKey, select, selectNew, reset, setId } =
    useMasterDetailSelection(ref, storages, (s) => s.storageId)

  useEffect(() => {
    dispatch(loadStorages())
  }, [dispatch])

  const list = (
    <List dense disablePadding>
      {storages.map((s) => (
        <SettingsListItem
          key={s.storageId}
          selected={selected?.storageId === s.storageId}
          onClick={() => select(s.storageId)}
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
      onAdd={selectNew}
      addTooltip={t('storage.newStorage')}
      list={list}
    >
      {isNew || selected ? (
        <StorageForm
          ref={detailRef}
          key={detailKey}
          storage={isNew ? null : selected}
          onSaved={setId}
          onRemoved={reset}
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
