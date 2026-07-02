import { useCallback, useEffect, useImperativeHandle, useState } from 'react'
import {
  Box,
  Button,
  Checkbox,
  Chip,
  Divider,
  FormControl,
  FormControlLabel,
  FormGroup,
  IconButton,
  InputLabel,
  List,
  ListItem,
  ListItemText,
  MenuItem,
  Select,
  Stack,
  Switch,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import DeleteIcon from '@mui/icons-material/Delete'
import EditIcon from '@mui/icons-material/Edit'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { setArmed as setArmedThunk, setStreamRecordingProfile } from '../store/liveSlice'
import { ANALYTICS_DETECTION_CLASSES, RECORDING_PROFILE_OFF_ID, RECORDING_PROFILE_PREALARM_ID } from '../dto/dtos'
import type { AnalyticsDetectionClass, AnalyticsWatchDto } from '../dto/dtos'

export type AnalyticsWatchesHandle = { applyIfDirty: () => Promise<boolean> }

const EMPTY_WATCH: AnalyticsWatchDto = {
  cameraId: '',
  streamName: '',
  enabled: true,
  classes: ['person'],
  minConfidence: 0.5,
  sampleFps: 1,
  armed: false,
  attachDebugCrops: false,
}

export default function AnalyticsWatchesPanel({ ref }: { ref?: React.Ref<AnalyticsWatchesHandle> }) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const allCameras = useAppSelector((s) => Object.values(s.live.cameraStatesById))

  const [watches, setWatches] = useState<AnalyticsWatchDto[]>([])
  const [editing, setEditing] = useState<AnalyticsWatchDto | null>(null)
  const [isNew, setIsNew] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

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

  useEffect(() => {
    if (!editing) return
    const handler = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = '' }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [editing])

  const startNew = () => {
    setEditing({ ...EMPTY_WATCH })
    setIsNew(true)
    setError(null)
  }

  const startEdit = (w: AnalyticsWatchDto) => {
    setEditing({ ...w })
    setIsNew(false)
    setError(null)
  }

  const cancel = () => { setEditing(null); setError(null) }

  const saveInternal = async (): Promise<boolean> => {
    if (!editing) return true
    setBusy(true)
    setError(null)
    try {
      const r = await fetch('/api/analytics-watches', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(editing),
      })
      const body = await r.json() as { success: boolean; message: string }
      if (!body.success) { setError(body.message); return false }
      setEditing(null)
      await load()
      return true
    } catch (e) {
      setError(String(e))
      return false
    } finally {
      setBusy(false)
    }
  }

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    await saveInternal()
  }

  useImperativeHandle(ref, () => ({
    applyIfDirty: () => editing?.cameraId ? saveInternal() : Promise.resolve(true),
  }))

  const remove = async (cameraId: string) => {
    setBusy(true)
    setError(null)
    try {
      const r = await fetch(`/api/analytics-watches/${encodeURIComponent(cameraId)}`, { method: 'DELETE' })
      const body = await r.json() as { success: boolean; message: string }
      if (!body.success) { setError(body.message); return }
      if (editing?.cameraId === cameraId) setEditing(null)
      await load()
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  // Same arm/disarm behavior as CameraUnifiedCard.armDisarm: besides camera_states.armed, arming
  // puts non-recording streams on the prealarm profile (and disarming reverts prealarm to off).
  // Without a baseline profile the stream pipeline is torn down whenever the alarm override
  // expires (recording off<->continuous flapping), which kills the MediaMTX push mid-stream and
  // the analytics RTSP reader with it.
  const setArmed = async (cameraId: string, armed: boolean) => {
    setBusy(true)
    setError(null)
    try {
      await dispatch(setArmedThunk({ cameraId, armed }))
      const camera = allCameras.find((c) => c.cameraId === cameraId)
      const isStreamRecording = (mode?: string) => (mode ?? 'off') !== 'off'
      for (const s of camera?.streams ?? []) {
        if (s.disabled) continue
        const effectiveMode = (s.overrideProfile ?? s.recordingProfile)?.mode
        if (armed) {
          if (!isStreamRecording(effectiveMode))
            await dispatch(setStreamRecordingProfile({ cameraId, streamName: s.streamName, profileId: RECORDING_PROFILE_PREALARM_ID }))
        } else if (s.recordingProfile?.id === RECORDING_PROFILE_PREALARM_ID) {
          await dispatch(setStreamRecordingProfile({ cameraId, streamName: s.streamName, profileId: RECORDING_PROFILE_OFF_ID }))
        }
      }
      await load()
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  const setField = <K extends keyof AnalyticsWatchDto>(key: K, value: AnalyticsWatchDto[K]) =>
    setEditing((prev) => prev ? { ...prev, [key]: value } : null)

  const toggleClass = (c: AnalyticsDetectionClass, checked: boolean) =>
    setEditing((prev) => prev
      ? { ...prev, classes: checked ? [...prev.classes, c] : prev.classes.filter((x) => x !== c) }
      : null)

  const handleCameraChange = (cameraId: string) => {
    const camera = allCameras.find((c) => c.cameraId === cameraId)
    const firstStream = camera?.streams?.[0]?.streamName ?? ''
    setEditing((prev) => prev ? { ...prev, cameraId, streamName: firstStream } : null)
  }

  const classLabel = (c: AnalyticsDetectionClass) => t(`analytics.class.${c}`)
  const cameraName = (cameraId: string) => allCameras.find((c) => c.cameraId === cameraId)?.name || cameraId

  const usedCameraIds = new Set(watches.map((w) => w.cameraId))
  const editingCamera = allCameras.find((c) => c.cameraId === editing?.cameraId)
  const availableCameras = isNew
    ? allCameras.filter((c) => !usedCameraIds.has(c.cameraId))
    : allCameras

  return (
    <Stack spacing={2} sx={{ maxWidth: 640 }}>
      <Typography variant="h6">{t('analytics.title')}</Typography>

      <List disablePadding>
        {watches.map((w, i) => (
          <Box key={w.cameraId}>
            {i > 0 && <Divider />}
            <ListItem
              disablePadding
              secondaryAction={
                <Stack direction="row" spacing={0.5} alignItems="center">
                  <Tooltip title={w.armed ? t('analytics.armedTooltip') : t('analytics.disarmedTooltip')}>
                    <Switch
                      size="small"
                      checked={w.armed}
                      onChange={(e) => void setArmed(w.cameraId, e.target.checked)}
                      disabled={busy}
                      sx={{ '& .MuiSwitch-switchBase.Mui-checked': { color: '#ff9800' }, '& .MuiSwitch-switchBase.Mui-checked + .MuiSwitch-track': { bgcolor: '#ff9800' } }}
                    />
                  </Tooltip>
                  <IconButton size="small" onClick={() => startEdit(w)} disabled={busy}>
                    <EditIcon fontSize="small" />
                  </IconButton>
                  <Tooltip title={t('analytics.deleteTooltip')}>
                    <span>
                      <IconButton size="small" color="error" onClick={() => void remove(w.cameraId)} disabled={busy}>
                        <DeleteIcon fontSize="small" />
                      </IconButton>
                    </span>
                  </Tooltip>
                </Stack>
              }
            >
              <ListItemText
                sx={{ py: 0.5, pl: 1 }}
                primary={
                  <Stack direction="row" spacing={0.5} alignItems="center" component="span">
                    <span>{cameraName(w.cameraId)}</span>
                    <Chip label={w.enabled ? t('analytics.enabled') : t('analytics.disabled')} size="small" color={w.enabled ? 'primary' : 'default'} variant="outlined" />
                    {w.enabled && !w.armed && (
                      <Chip label={t('analytics.notArmed')} size="small" color="warning" variant="outlined" />
                    )}
                  </Stack>
                }
                secondary={
                  <Stack direction="row" spacing={0.5} alignItems="center" flexWrap="wrap" component="span">
                    {w.classes.map((c) => <Chip key={c} label={classLabel(c)} size="small" />)}
                    <Typography variant="caption" color="text.secondary">
                      {w.streamName}
                    </Typography>
                  </Stack>
                }
              />
            </ListItem>
          </Box>
        ))}
        {watches.length === 0 && (
          <Typography variant="body2" color="text.secondary" sx={{ pl: 1, py: 1 }}>
            {t('analytics.noWatches')}
          </Typography>
        )}
      </List>

      {!editing && (
        <Box>
          <Button variant="outlined" size="small" onClick={startNew}>
            {t('analytics.addWatch')}
          </Button>
        </Box>
      )}

      {editing && (
        <Stack component="form" spacing={2} onSubmit={(e) => void save(e)}
          sx={{ pt: 1, borderTop: 1, borderColor: 'divider' }}>
          <Typography variant="subtitle2">
            {isNew ? t('analytics.newWatch') : t('analytics.editWatch')}
          </Typography>

          <FormControl size="small" fullWidth disabled={!isNew}>
            <InputLabel>{t('analytics.camera')}</InputLabel>
            <Select
              value={editing.cameraId}
              label={t('analytics.camera')}
              onChange={(e) => handleCameraChange(e.target.value)}
            >
              {availableCameras.map((c) => (
                <MenuItem key={c.cameraId} value={c.cameraId}>{c.name || c.cameraId}</MenuItem>
              ))}
            </Select>
          </FormControl>

          <FormControl size="small" fullWidth disabled={!editing.cameraId}>
            <InputLabel>{t('analytics.stream')}</InputLabel>
            <Select
              value={editing.streamName}
              label={t('analytics.stream')}
              onChange={(e) => setField('streamName', e.target.value)}
            >
              {(editingCamera?.streams ?? []).map((s) => (
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
                      checked={editing.classes.includes(c)}
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
              value={editing.minConfidence}
              onChange={(e) => setField('minConfidence', Math.min(1, Math.max(0, Number(e.target.value))))}
              size="small"
              slotProps={{ htmlInput: { min: 0, max: 1, step: 0.05 } }}
              sx={{ width: 160 }}
            />
            <TextField
              label={t('analytics.sampleFps')}
              type="number"
              value={editing.sampleFps}
              onChange={(e) => setField('sampleFps', Math.max(1, Math.floor(Number(e.target.value))))}
              size="small"
              slotProps={{ htmlInput: { min: 1 } }}
              sx={{ width: 140 }}
            />
          </Stack>

          <FormControlLabel
            control={
              <Switch
                size="small"
                checked={editing.enabled}
                onChange={(e) => setField('enabled', e.target.checked)}
              />
            }
            label={<Typography variant="body2">{t('analytics.enabled')}</Typography>}
          />

          <FormControlLabel
            control={
              <Switch
                size="small"
                checked={editing.attachDebugCrops}
                onChange={(e) => setField('attachDebugCrops', e.target.checked)}
              />
            }
            label={<Typography variant="body2">{t('analytics.debugCrops')}</Typography>}
          />

          {error && (
            <Typography variant="caption" color="error">{error}</Typography>
          )}

          <Stack direction="row" spacing={1}>
            <Button type="submit" variant="outlined" size="small" disabled={busy || !editing.cameraId || !editing.streamName || editing.classes.length === 0}>
              {isNew ? t('analytics.create') : t('common.apply')}
            </Button>
            <Button size="small" onClick={cancel} disabled={busy}>{t('common.cancel')}</Button>
          </Stack>
        </Stack>
      )}

      {error && !editing && (
        <Typography variant="caption" color="error">{error}</Typography>
      )}
    </Stack>
  )
}
