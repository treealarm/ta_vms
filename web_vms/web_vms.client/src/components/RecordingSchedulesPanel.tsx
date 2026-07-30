import { useEffect, useState } from 'react'
import { Box, Chip, List, Stack, TextField, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { loadSchedules, removeSchedule, upsertSchedule } from '../store/scheduleSlice'
import type { RecordingScheduleDto } from '../dto/dtos'
import ScheduleWeekGrid from './ScheduleWeekGrid'
import DetailToolbar from './settings/DetailToolbar'
import MasterDetailLayout from './settings/MasterDetailLayout'
import SettingsListItem from './settings/SettingsListItem'
import { useDirtyGuard } from './settings/useDirtyGuard'
import type { ApplyIfDirtyHandle } from './settings/useDirtyGuard'
import { useMasterDetailSelection } from './settings/useMasterDetailSelection'

function localTimezone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC'
  } catch {
    return 'UTC'
  }
}

const emptySchedule = (): RecordingScheduleDto => ({
  id: '',
  name: '',
  timezone: localTimezone(),
  leadInSeconds: 5,
  windows: [],
})

const sameWindows = (a: RecordingScheduleDto, b: RecordingScheduleDto) =>
  JSON.stringify(a.windows) === JSON.stringify(b.windows)

type FormProps = {
  ref?: React.Ref<ApplyIfDirtyHandle>
  schedule: RecordingScheduleDto | null  // null = new
  onSaved: (isNew: boolean) => Promise<void>
  onRemoved: () => Promise<void>
}

function RecordingScheduleForm({ ref, schedule, onSaved, onRemoved }: FormProps) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const isNew = schedule === null

  const [form, setForm] = useState<RecordingScheduleDto>(schedule ?? emptySchedule())
  const [snap, setSnap] = useState<RecordingScheduleDto>(schedule ?? emptySchedule())
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const dirty = !isNew && (
    form.name !== snap.name
    || form.timezone !== snap.timezone
    || form.leadInSeconds !== snap.leadInSeconds
    || !sameWindows(form, snap)
  )

  const applyInternal = async (): Promise<boolean> => {
    if (!isNew && !dirty) return true
    setBusy(true)
    setError(null)
    try {
      const result = await dispatch(upsertSchedule(form)).unwrap()
      if (!result.success) { setError(result.message); return false }
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

  const handleRemove = async () => {
    if (!form.id) return
    setBusy(true)
    setError(null)
    try {
      const result = await dispatch(removeSchedule(form.id)).unwrap()
      if (!result.success) { setError(result.message); return }
      await onRemoved()
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  const setField = <K extends keyof RecordingScheduleDto>(key: K, value: RecordingScheduleDto[K]) =>
    setForm((prev) => ({ ...prev, [key]: value }))

  // For a new form "dirty" means "complete enough to save" — navigating away then creates it
  // instead of silently discarding the entered data.
  useDirtyGuard(ref, isNew ? Boolean(form.name.trim()) : dirty, applyInternal)

  return (
    <Box
      component="form"
      onSubmit={(e) => { e.preventDefault(); void applyInternal() }}
      sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}
    >
      <DetailToolbar
        title={isNew ? t('schedule.newSchedule') : schedule.name}
        dirty={isNew ? Boolean(form.name.trim()) : dirty}
        busy={busy}
        applyLabel={isNew ? t('schedule.create') : undefined}
        onCancel={isNew ? undefined : () => setForm({ ...snap })}
        onDelete={isNew ? undefined : () => void handleRemove()}
        deleteLabel={t('schedule.deleteSchedule')}
      />

      <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', p: 2 }}>
        <Stack spacing={2}>
          <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap>
            <TextField
              label={t('common.name')}
              value={form.name}
              onChange={(e) => setField('name', e.target.value)}
              required
              size="small"
              sx={{ width: 260 }}
            />
            <TextField
              label={t('schedule.timezone')}
              value={form.timezone}
              onChange={(e) => setField('timezone', e.target.value)}
              size="small"
              helperText={t('schedule.timezoneHint')}
              sx={{ width: 220 }}
            />
            <TextField
              label={t('schedule.leadInSec')}
              type="number"
              value={form.leadInSeconds}
              onChange={(e) => {
                const v = Number(e.target.value)
                if (!Number.isNaN(v)) setField('leadInSeconds', Math.max(0, Math.floor(v)))
              }}
              size="small"
              slotProps={{ htmlInput: { min: 0 } }}
              helperText={t('schedule.leadInHint')}
              sx={{ width: 200 }}
            />
          </Stack>

          <Typography variant="caption" color="text.secondary">
            {t('schedule.effectHint')}
          </Typography>

          <ScheduleWeekGrid
            windows={form.windows}
            onChange={(windows) => setField('windows', windows)}
          />

          {error && <Typography variant="caption" color="error">{error}</Typography>}
        </Stack>
      </Box>
    </Box>
  )
}

export default function RecordingSchedulesPanel({ ref }: { ref?: React.Ref<ApplyIfDirtyHandle> }) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const schedules = useAppSelector((s) => s.schedule.schedules)
  const status = useAppSelector((s) => s.schedule.status)

  const { isNew, selected, detailRef, detailKey, select, selectNew, reset } =
    useMasterDetailSelection(ref, schedules, (s) => s.id ?? '')

  useEffect(() => { void dispatch(loadSchedules()) }, [dispatch])

  const list = (
    <List dense disablePadding>
      {schedules.map((s) => (
        <SettingsListItem
          key={s.id}
          selected={selected?.id === s.id}
          onClick={() => select(s.id ?? '')}
          wrap
          primary={s.name}
          secondary={
            <Stack direction="row" spacing={0.5} alignItems="center" flexWrap="wrap" component="span">
              <Chip label={s.timezone} size="small" variant="outlined" />
              <Typography variant="caption" color="text.secondary">
                {t('schedule.windowCount', { count: s.windows.length })}
              </Typography>
            </Stack>
          }
        />
      ))}
      {schedules.length === 0 && (
        <Typography variant="body2" color="text.secondary" sx={{ p: 1.5 }}>
          {t('schedule.noSchedules')}
        </Typography>
      )}
    </List>
  )

  return (
    <MasterDetailLayout
      listTitle={t('schedule.title')}
      onAdd={selectNew}
      addTooltip={t('schedule.addSchedule')}
      list={list}
    >
      {isNew || selected ? (
        <RecordingScheduleForm
          ref={detailRef}
          key={detailKey}
          schedule={isNew ? null : selected}
          onSaved={async (created) => { if (created) reset() }}
          onRemoved={async () => reset()}
        />
      ) : (
        <Box sx={{ p: 2 }}>
          <Typography color="text.secondary">{t('schedule.selectHint')}</Typography>
          {status && <Typography variant="caption" color="error" display="block" sx={{ mt: 1 }}>{status}</Typography>}
        </Box>
      )}
    </MasterDetailLayout>
  )
}
