import { useCallback, useEffect, useState } from 'react'
import {
  Box,
  Checkbox,
  Chip,
  FormControl,
  FormControlLabel,
  FormGroup,
  InputLabel,
  List,
  MenuItem,
  Select,
  Stack,
  Switch,
  TextField,
  Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppSelector } from '../store'
import { ANALYTICS_DETECTION_CLASSES } from '../dto/dtos'
import type { AnalyticsDetectionClass, AnalyticsWatchDto, CameraSetupDto } from '../dto/dtos'
import DetailToolbar from './settings/DetailToolbar'
import MasterDetailLayout from './settings/MasterDetailLayout'
import SettingsListItem from './settings/SettingsListItem'
import { useDirtyGuard } from './settings/useDirtyGuard'
import type { ApplyIfDirtyHandle } from './settings/useDirtyGuard'
import { useMasterDetailSelection } from './settings/useMasterDetailSelection'

const EMPTY_WATCH: AnalyticsWatchDto = {
  cameraId: '',
  streamName: '',
  enabled: true,
  classes: ['person'],
  minConfidence: 0.5,
  sampleFps: 1,
  armed: false,
  attachDebugCrops: false,
  timeoutSeconds: 10,
}

type FormProps = {
  ref?: React.Ref<ApplyIfDirtyHandle>
  watch: AnalyticsWatchDto | null  // null = new
  allCameras: CameraSetupDto[]
  takenCameraIds: string[]         // cameras that already have a watch (for the new-watch camera select)
  toolbarExtra?: React.ReactNode   // arm/disarm switch, owned by the container
  onSaved: (isNew: boolean) => Promise<void>
  onRemoved: () => Promise<void>
}

function AnalyticsWatchForm({ ref, watch, allCameras, takenCameraIds, toolbarExtra, onSaved, onRemoved }: FormProps) {
  const { t } = useTranslation()
  const isNew = watch === null

  const [form, setForm] = useState<AnalyticsWatchDto>(watch ?? { ...EMPTY_WATCH })
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Snapshot of the saved state — used for dirty detection and Cancel
  const [snap, setSnap] = useState<AnalyticsWatchDto>(watch ?? { ...EMPTY_WATCH })

  const classesChanged =
    form.classes.length !== snap.classes.length
    || form.classes.some((c) => !snap.classes.includes(c))

  const dirty = !isNew && (
    form.streamName !== snap.streamName
    || form.enabled !== snap.enabled
    || classesChanged
    || form.minConfidence !== snap.minConfidence
    || form.sampleFps !== snap.sampleFps
    || form.attachDebugCrops !== snap.attachDebugCrops
    || form.timeoutSeconds !== snap.timeoutSeconds
  )

  const valid = Boolean(form.cameraId && form.streamName && form.classes.length > 0)

  const applyInternal = async (): Promise<boolean> => {
    if (!isNew && !dirty) return true
    if (!valid) {
      setError(t('analytics.invalidWatch'))
      return false
    }
    setBusy(true)
    setError(null)
    try {
      // `armed` belongs to the camera, not to this form, and is set from the operator's
      // camera card. Send whatever the store currently holds rather than the snapshot this
      // form mounted with, so saving a watch cannot silently disarm a camera someone armed
      // while the form was open.
      const payload: AnalyticsWatchDto = { ...form, armed: watch?.armed ?? form.armed }
      const r = await fetch('/api/analytics-watches', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
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
      const r = await fetch(`/api/analytics-watches/${encodeURIComponent(form.cameraId)}`, { method: 'DELETE' })
      const body = await r.json() as { success: boolean; message: string }
      if (!body.success) { setError(body.message); return }
      await onRemoved()
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  const setField = <K extends keyof AnalyticsWatchDto>(key: K, value: AnalyticsWatchDto[K]) =>
    setForm((prev) => ({ ...prev, [key]: value }))

  const toggleClass = (c: AnalyticsDetectionClass, checked: boolean) =>
    setForm((prev) => ({ ...prev, classes: checked ? [...prev.classes, c] : prev.classes.filter((x) => x !== c) }))

  const handleCameraChange = (cameraId: string) => {
    const camera = allCameras.find((c) => c.cameraId === cameraId)
    const firstStream = camera?.streams?.[0]?.streamName ?? ''
    setForm((prev) => ({ ...prev, cameraId, streamName: firstStream }))
  }

  const classLabel = (c: AnalyticsDetectionClass) => t(`analytics.class.${c}`)

  const formCamera = allCameras.find((c) => c.cameraId === form.cameraId)
  const cameraOptions = isNew
    ? allCameras.filter((c) => !takenCameraIds.includes(c.cameraId))
    : allCameras

  const cameraName = formCamera?.name || form.cameraId

  // For a new form "dirty" means "complete enough to save" — navigation then
  // auto-creates it instead of silently discarding the entered data
  useDirtyGuard(ref, isNew ? valid : dirty, applyInternal)

  return (
    <Box component="form" onSubmit={(e) => void handleApply(e)} sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
      <DetailToolbar
        title={isNew ? t('analytics.newWatch') : cameraName}
        subtitle={isNew ? undefined : form.streamName}
        dirty={isNew ? valid : dirty}
        busy={busy}
        applyLabel={isNew ? t('analytics.create') : undefined}
        onCancel={isNew ? undefined : handleCancel}
        onDelete={isNew ? undefined : () => void handleRemove()}
        deleteLabel={t('analytics.deleteWatch')}
        extra={toolbarExtra}
      />

      <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', p: 2 }}>
        <Stack spacing={2} sx={{ maxWidth: 480 }}>
          <FormControl size="small" fullWidth disabled={!isNew}>
            <InputLabel>{t('analytics.camera')}</InputLabel>
            <Select
              value={form.cameraId}
              label={t('analytics.camera')}
              onChange={(e) => handleCameraChange(e.target.value)}
            >
              {cameraOptions.map((c) => (
                <MenuItem key={c.cameraId} value={c.cameraId}>{c.name || c.cameraId}</MenuItem>
              ))}
            </Select>
          </FormControl>

          <FormControl size="small" fullWidth disabled={!form.cameraId}>
            <InputLabel>{t('analytics.stream')}</InputLabel>
            <Select
              value={form.streamName}
              label={t('analytics.stream')}
              onChange={(e) => setField('streamName', e.target.value)}
            >
              {(formCamera?.streams ?? []).map((s) => (
                <MenuItem key={s.streamName} value={s.streamName}>{s.streamName}</MenuItem>
              ))}
            </Select>
          </FormControl>

          <Box>
            <Typography variant="caption" color="text.secondary">{t('analytics.classes')}</Typography>
            <FormGroup row>
              {ANALYTICS_DETECTION_CLASSES.map((c) => (
                <FormControlLabel
                  key={c}
                  control={
                    <Checkbox
                      size="small"
                      checked={form.classes.includes(c)}
                      onChange={(e) => toggleClass(c, e.target.checked)}
                    />
                  }
                  label={<Typography variant="body2">{classLabel(c)}</Typography>}
                />
              ))}
            </FormGroup>
          </Box>

          <Stack direction="row" spacing={1}>
            <TextField
              label={t('analytics.minConfidence')}
              type="number"
              value={form.minConfidence}
              onChange={(e) => setField('minConfidence', Math.min(1, Math.max(0, Number(e.target.value))))}
              size="small"
              slotProps={{ htmlInput: { min: 0, max: 1, step: 0.05 } }}
              sx={{ width: 160 }}
            />
            <TextField
              label={t('analytics.sampleFps')}
              type="number"
              value={form.sampleFps}
              onChange={(e) => setField('sampleFps', Math.max(1, Math.floor(Number(e.target.value))))}
              size="small"
              slotProps={{ htmlInput: { min: 1 } }}
              sx={{ width: 140 }}
            />
            <TextField
              label={t('analytics.timeoutSeconds')}
              helperText={t('analytics.timeoutSecondsHelper')}
              type="number"
              value={form.timeoutSeconds}
              onChange={(e) => setField('timeoutSeconds', Math.max(1, Math.floor(Number(e.target.value))))}
              size="small"
              slotProps={{ htmlInput: { min: 1 } }}
              sx={{ width: 180 }}
            />
          </Stack>

          <FormControlLabel
            control={
              <Switch
                size="small"
                checked={form.enabled}
                onChange={(e) => setField('enabled', e.target.checked)}
              />
            }
            label={<Typography variant="body2">{t('analytics.enabled')}</Typography>}
          />

          <FormControlLabel
            control={
              <Switch
                size="small"
                checked={form.attachDebugCrops}
                onChange={(e) => setField('attachDebugCrops', e.target.checked)}
              />
            }
            label={<Typography variant="body2">{t('analytics.debugCrops')}</Typography>}
          />

          {error && (
            <Typography variant="caption" color="error">{error}</Typography>
          )}
        </Stack>
      </Box>
    </Box>
  )
}

export default function AnalyticsWatchesPanel({ ref }: { ref?: React.Ref<ApplyIfDirtyHandle> }) {
  const { t } = useTranslation()
  const allCameras = useAppSelector((s) => Object.values(s.live.cameraStatesById))

  const [watches, setWatches] = useState<AnalyticsWatchDto[]>([])
  const [error, setError] = useState<string | null>(null)

  const { isNew, selected, detailRef, detailKey, select, selectNew, reset } =
    useMasterDetailSelection(ref, watches, (w) => w.cameraId)

  const load = useCallback(async () => {
    try {
      const r = await fetch('/api/analytics-watches')
      if (!r.ok) throw new Error(`HTTP ${r.status}`)
      setWatches(await r.json() as AnalyticsWatchDto[])
    } catch (e) {
      setError(String(e))
    }
  }, [])

  useEffect(() => { void load() }, [load])

  const classLabel = (c: AnalyticsDetectionClass) => t(`analytics.class.${c}`)
  const cameraName = (cameraId: string) => allCameras.find((c) => c.cameraId === cameraId)?.name || cameraId

  const takenCameraIds = watches.map((w) => w.cameraId)
  const availableCameras = allCameras.filter((c) => !takenCameraIds.includes(c.cameraId))

  const list = (
    <List dense disablePadding>
      {watches.map((w) => (
        <SettingsListItem
          key={w.cameraId}
          selected={selected?.cameraId === w.cameraId}
          onClick={() => select(w.cameraId)}
          wrap
          primary={
            <Stack direction="row" spacing={0.5} alignItems="center" flexWrap="wrap" component="span">
              <span>{cameraName(w.cameraId)}</span>
              <Chip label={w.enabled ? t('analytics.enabled') : t('analytics.disabled')} size="small" color={w.enabled ? 'primary' : 'default'} variant="outlined" />
            </Stack>
          }
          secondary={
            <Stack direction="row" spacing={0.5} alignItems="center" flexWrap="wrap" component="span" sx={{ rowGap: 0.5 }}>
              {w.classes.map((c) => <Chip key={c} label={classLabel(c)} size="small" />)}
              <Typography variant="caption" color="text.secondary">
                {w.streamName}
              </Typography>
            </Stack>
          }
        />
      ))}
      {watches.length === 0 && (
        <Typography variant="body2" color="text.secondary" sx={{ p: 1.5 }}>
          {t('analytics.noWatches')}
        </Typography>
      )}
    </List>
  )

  return (
    <MasterDetailLayout
      listTitle={t('analytics.title')}
      onAdd={selectNew}
      addTooltip={t('analytics.addWatch')}
      addDisabled={availableCameras.length === 0}
      list={list}
    >
      {isNew || selected ? (
        <AnalyticsWatchForm
          ref={detailRef}
          key={detailKey}
          watch={isNew ? null : selected}
          allCameras={allCameras}
          takenCameraIds={takenCameraIds}
          toolbarExtra={selected ? (
            <Stack direction="row" spacing={1} alignItems="center">
              {/* Arm failures must be visible next to the switch that caused them */}
              {error && <Typography variant="caption" color="error">{error}</Typography>}
            </Stack>
          ) : undefined}
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
          <Typography color="text.secondary">{t('analytics.selectHint')}</Typography>
          {error && <Typography variant="caption" color="error" display="block" sx={{ mt: 1 }}>{error}</Typography>}
        </Box>
      )}
    </MasterDetailLayout>
  )
}
