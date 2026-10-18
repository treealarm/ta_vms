import { useCallback, useEffect, useState } from 'react'
import {
  Box,
  Chip,
  FormControlLabel,
  List,
  MenuItem,
  Stack,
  Switch,
  TextField,
  Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'
import type { RecordingProfileDto, RecordingMode } from '../dto/dtos'
import DetailToolbar from './settings/DetailToolbar'
import MasterDetailLayout from './settings/MasterDetailLayout'
import SettingsListItem from './settings/SettingsListItem'
import { useDirtyGuard } from './settings/useDirtyGuard'
import type { ApplyIfDirtyHandle } from './settings/useDirtyGuard'
import { useMasterDetailSelection } from './settings/useMasterDetailSelection'

const EMPTY_PROFILE: RecordingProfileDto = {
  id: '',
  name: '',
  mode: 'continuous',
  preRecordSeconds: 0,
  postRecordSeconds: 0,
  segmentSeconds: 60,
  isOverridePreset: false,
  isBuiltin: false,
}

type FormProps = {
  ref?: React.Ref<ApplyIfDirtyHandle>
  profile: RecordingProfileDto | null  // null = new
  onSaved: (isNew: boolean) => Promise<void>
  onRemoved: () => Promise<void>
}

function RecordingProfileForm({ ref, profile, onSaved, onRemoved }: FormProps) {
  const { t } = useTranslation()
  const isNew = profile === null
  const readOnly = profile?.isBuiltin === true

  const [form, setForm] = useState<RecordingProfileDto>(profile ?? { ...EMPTY_PROFILE })
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Snapshot of the saved state — used for dirty detection and Cancel
  const [snap, setSnap] = useState<RecordingProfileDto>(profile ?? { ...EMPTY_PROFILE })

  const dirty = !isNew && !readOnly && (
    form.name !== snap.name
    || form.mode !== snap.mode
    || form.preRecordSeconds !== snap.preRecordSeconds
    || form.postRecordSeconds !== snap.postRecordSeconds
    || form.segmentSeconds !== snap.segmentSeconds
    || form.isOverridePreset !== snap.isOverridePreset
  )

  const applyInternal = async (): Promise<boolean> => {
    if (!isNew && !dirty) return true
    setBusy(true)
    setError(null)
    try {
      const r = await fetch('/api/recording-profiles', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(form),
      })
      const body = await r.json() as { success: boolean; message: string }
      if (!body.success) { setError(body.message); return false }
      setSnap({ ...form })
      await onSaved(isNew)
      return true
    } catch (e) {
      setError(String(e))
      return false
    } finally {
      setBusy(false)
    }
  }

  const handleApply = async (e: React.FormEvent) => {
    e.preventDefault()
    await applyInternal()
  }

  const handleCancel = () => setForm({ ...snap })

  const handleRemove = async () => {
    setBusy(true)
    setError(null)
    try {
      const r = await fetch(`/api/recording-profiles/${encodeURIComponent(form.id)}`, { method: 'DELETE' })
      const body = await r.json() as { success: boolean; message: string }
      if (!body.success) { setError(body.message); return }
      await onRemoved()
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  const setField = <K extends keyof RecordingProfileDto>(key: K, value: RecordingProfileDto[K]) =>
    setForm((prev) => ({ ...prev, [key]: value }))

  const setNonNeg = (key: 'preRecordSeconds' | 'postRecordSeconds' | 'segmentSeconds', raw: string) => {
    const v = Number(raw)
    if (!Number.isNaN(v)) setField(key, Math.max(0, Math.floor(v)))
  }

  // For a new form "dirty" means "complete enough to save" — navigation then
  // auto-creates it instead of silently discarding the entered data
  useDirtyGuard(ref, isNew ? Boolean(form.name.trim()) : dirty, applyInternal)

  return (
    <Box component="form" onSubmit={(e) => void handleApply(e)} sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
      <DetailToolbar
        title={isNew ? t('recording.newProfile') : profile.name}
        dirty={isNew ? Boolean(form.name.trim()) : dirty}
        busy={busy}
        applyLabel={isNew ? t('recording.create') : undefined}
        hideApply={readOnly}
        onCancel={isNew || readOnly ? undefined : handleCancel}
        onDelete={isNew || readOnly ? undefined : () => void handleRemove()}
        deleteLabel={t('recording.deleteProfile')}
        extra={readOnly ? <Chip label={t('recording.builtinChip')} size="small" variant="outlined" /> : undefined}
      />

      <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', p: 2 }}>
        <Stack spacing={2} sx={{ maxWidth: 420 }}>
          <TextField
            label={t('common.name')}
            value={form.name}
            onChange={(e) => setField('name', e.target.value)}
            required
            size="small"
            disabled={readOnly}
            sx={{ maxWidth: 260 }}
          />

          <TextField
            select
            label={t('recording.mode')}
            value={form.mode}
            onChange={(e) => setField('mode', e.target.value as RecordingMode)}
            size="small"
            disabled={readOnly}
            sx={{ maxWidth: 200 }}
          >
            <MenuItem value="continuous">{t('recording.continuous')}</MenuItem>
            <MenuItem value="prealarm">{t('recording.prealarm')}</MenuItem>
            {readOnly && form.mode === 'off' && <MenuItem value="off">{t('recording.off')}</MenuItem>}
          </TextField>

          {form.mode === 'prealarm' && (
            <Stack direction="row" spacing={1}>
              <TextField
                label={t('recording.preRecordSec')}
                type="number"
                value={form.preRecordSeconds}
                onChange={(e) => setNonNeg('preRecordSeconds', e.target.value)}
                size="small"
                disabled={readOnly}
                slotProps={{ htmlInput: { min: 0 } }}
                sx={{ width: 140 }}
              />
              <TextField
                label={t('recording.postRecordSec')}
                type="number"
                value={form.postRecordSeconds}
                onChange={(e) => setNonNeg('postRecordSeconds', e.target.value)}
                size="small"
                disabled={readOnly}
                slotProps={{ htmlInput: { min: 0 } }}
                sx={{ width: 140 }}
              />
            </Stack>
          )}

          <TextField
            label={t('recording.segmentSec')}
            type="number"
            value={form.segmentSeconds}
            onChange={(e) => setNonNeg('segmentSeconds', e.target.value)}
            size="small"
            disabled={readOnly}
            slotProps={{ htmlInput: { min: 1 } }}
            sx={{ width: 140 }}
          />

          <FormControlLabel
            control={
              <Switch
                size="small"
                checked={form.isOverridePreset}
                onChange={(e) => setField('isOverridePreset', e.target.checked)}
                disabled={readOnly}
              />
            }
            label={<Typography variant="body2">{t('recording.overridePreset')}</Typography>}
          />

          {error && (
            <Typography variant="caption" color="error">{error}</Typography>
          )}
        </Stack>
      </Box>
    </Box>
  )
}

export default function RecordingProfilesPanel({ ref }: { ref?: React.Ref<ApplyIfDirtyHandle> }) {
  const { t } = useTranslation()
  const [profiles, setProfiles] = useState<RecordingProfileDto[]>([])
  const [error, setError] = useState<string | null>(null)

  const { isNew, selected, detailRef, detailKey, select, selectNew, reset } =
    useMasterDetailSelection(ref, profiles, (p) => p.id)

  const load = useCallback(async () => {
    try {
      const r = await fetch('/api/recording-profiles')
      if (!r.ok) throw new Error(`HTTP ${r.status}`)
      setProfiles(await r.json() as RecordingProfileDto[])
    } catch (e) {
      setError(String(e))
    }
  }, [])

  useEffect(() => { void load() }, [load])

  const modeLabel = (mode: RecordingMode) =>
    mode === 'continuous' ? t('recording.continuous') : mode === 'prealarm' ? t('recording.prealarm') : t('recording.off')

  const list = (
    <List dense disablePadding>
      {profiles.map((p) => (
        <SettingsListItem
          key={p.id}
          selected={selected?.id === p.id}
          onClick={() => select(p.id)}
          wrap
          primary={
            <Stack direction="row" spacing={0.5} alignItems="center" flexWrap="wrap" component="span">
              <span>{p.name}</span>
              {p.isBuiltin && <Chip label={t('recording.builtinChip')} size="small" variant="outlined" />}
              {p.isOverridePreset && !p.isBuiltin && <Chip label={t('recording.overridePresetChip')} size="small" color="primary" variant="outlined" />}
            </Stack>
          }
          secondary={
            <Stack direction="row" spacing={0.5} alignItems="center" flexWrap="wrap" component="span">
              <Chip label={modeLabel(p.mode)} size="small" />
              {p.mode === 'prealarm' && (
                <Typography variant="caption" color="text.secondary">
                  {t('recording.prePostSeg', { pre: p.preRecordSeconds, post: p.postRecordSeconds, seg: p.segmentSeconds })}
                </Typography>
              )}
              {p.mode === 'continuous' && (
                <Typography variant="caption" color="text.secondary">
                  {t('recording.seg', { seg: p.segmentSeconds })}
                </Typography>
              )}
            </Stack>
          }
        />
      ))}
      {profiles.length === 0 && (
        <Typography variant="body2" color="text.secondary" sx={{ p: 1.5 }}>
          {t('recording.noProfiles')}
        </Typography>
      )}
    </List>
  )

  return (
    <MasterDetailLayout
      listTitle={t('recording.title')}
      onAdd={selectNew}
      addTooltip={t('recording.addProfile')}
      list={list}
    >
      {isNew || selected ? (
        <RecordingProfileForm
          ref={detailRef}
          key={detailKey}
          profile={isNew ? null : selected}
          onSaved={async (created) => {
            await load()
            if (created) reset()
          }}
          onRemoved={async () => {
            await load()
            reset()
          }}
        />
      ) : (
        <Box sx={{ p: 2 }}>
          <Typography color="text.secondary">{t('recording.selectHint')}</Typography>
          {error && <Typography variant="caption" color="error" display="block" sx={{ mt: 1 }}>{error}</Typography>}
        </Box>
      )}
    </MasterDetailLayout>
  )
}
