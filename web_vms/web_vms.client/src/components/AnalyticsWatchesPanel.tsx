import { useCallback, useEffect, useImperativeHandle, useState } from 'react'
import {
  Box,
  Button,
  Chip,
  Divider,
  FormControlLabel,
  IconButton,
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
import type { SelectChangeEvent } from '@mui/material'
import DeleteIcon from '@mui/icons-material/Delete'
import EditIcon from '@mui/icons-material/Edit'
import { useTranslation } from 'react-i18next'
import { useAppSelector } from '../store'
import { ANALYTICS_DETECTION_CLASSES } from '../dto/dtos'
import type { AnalyticsDetectionClass, AnalyticsWatchDto } from '../dto/dtos'

export type AnalyticsWatchesHandle = { applyIfDirty: () => Promise<boolean> }

const EMPTY_WATCH: Omit<AnalyticsWatchDto, 'id'> = {
  rtspUrl: '',
  credUser: '',
  credPass: '',
  enabled: false,
  classes: ['person'],
  minConfidence: 0.5,
  sampleFps: 1,
  cameras: [],
}

export default function AnalyticsWatchesPanel({ ref }: { ref?: React.Ref<AnalyticsWatchesHandle> }) {
  const { t } = useTranslation()
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
    setEditing({ id: '', ...EMPTY_WATCH })
    setIsNew(true)
    setError(null)
  }

  const startEdit = (w: AnalyticsWatchDto) => {
    setEditing({ ...w, cameras: w.cameras.map((c) => ({ ...c })) })
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
    applyIfDirty: () => editing?.rtspUrl ? saveInternal() : Promise.resolve(true),
  }))

  const remove = async (id: string) => {
    setBusy(true)
    setError(null)
    try {
      const r = await fetch(`/api/analytics-watches/${encodeURIComponent(id)}`, { method: 'DELETE' })
      const body = await r.json() as { success: boolean; message: string }
      if (!body.success) { setError(body.message); return }
      if (editing?.id === id) setEditing(null)
      await load()
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  const setField = <K extends keyof AnalyticsWatchDto>(key: K, value: AnalyticsWatchDto[K]) =>
    setEditing((prev) => prev ? { ...prev, [key]: value } : null)

  const handleClassesChange = (e: SelectChangeEvent<AnalyticsDetectionClass[]>) => {
    const value = e.target.value
    setField('classes', typeof value === 'string' ? value.split(',') as AnalyticsDetectionClass[] : value)
  }

  const handleCamerasChange = (e: SelectChangeEvent<string[]>) => {
    const value = e.target.value
    const ids = typeof value === 'string' ? value.split(',') : value
    setField('cameras', ids.map((cameraId) => ({ cameraId, armed: false })))
  }

  const classLabel = (c: AnalyticsDetectionClass) => t(`analytics.class.${c}`)
  const cameraName = (cameraId: string) => allCameras.find((c) => c.cameraId === cameraId)?.name || cameraId

  return (
    <Stack spacing={2} sx={{ maxWidth: 640 }}>
      <Typography variant="h6">{t('analytics.title')}</Typography>

      <List disablePadding>
        {watches.map((w, i) => (
          <Box key={w.id}>
            {i > 0 && <Divider />}
            <ListItem
              disablePadding
              secondaryAction={
                <Stack direction="row" spacing={0.5}>
                  <IconButton size="small" onClick={() => startEdit(w)} disabled={busy}>
                    <EditIcon fontSize="small" />
                  </IconButton>
                  <Tooltip title={t('analytics.deleteTooltip')}>
                    <span>
                      <IconButton size="small" color="error" onClick={() => void remove(w.id)} disabled={busy}>
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
                    <span>{w.rtspUrl}</span>
                    <Chip label={w.enabled ? t('analytics.enabled') : t('analytics.disabled')} size="small" color={w.enabled ? 'primary' : 'default'} variant="outlined" />
                  </Stack>
                }
                secondary={
                  <Stack direction="row" spacing={0.5} alignItems="center" flexWrap="wrap" component="span">
                    {w.classes.map((c) => <Chip key={c} label={classLabel(c)} size="small" />)}
                    <Typography variant="caption" color="text.secondary">
                      {w.cameras.length > 0
                        ? w.cameras.map((c) => cameraName(c.cameraId)).join(', ')
                        : t('analytics.noCameras')}
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

          <TextField
            label={t('analytics.rtspUrl')}
            value={editing.rtspUrl}
            onChange={(e) => setField('rtspUrl', e.target.value)}
            required
            size="small"
            fullWidth
          />

          <Stack direction="row" spacing={1}>
            <TextField
              label={t('analytics.credUser')}
              value={editing.credUser ?? ''}
              onChange={(e) => setField('credUser', e.target.value)}
              size="small"
              sx={{ width: 200 }}
            />
            <TextField
              label={t('analytics.credPass')}
              type="password"
              value={editing.credPass ?? ''}
              onChange={(e) => setField('credPass', e.target.value)}
              size="small"
              sx={{ width: 200 }}
            />
          </Stack>

          <Select
            multiple
            value={editing.classes}
            onChange={handleClassesChange}
            size="small"
            displayEmpty
            renderValue={(selected) => selected.map(classLabel).join(', ') || t('analytics.selectClasses')}
            sx={{ maxWidth: 400 }}
          >
            {ANALYTICS_DETECTION_CLASSES.map((c) => (
              <MenuItem key={c} value={c}>{classLabel(c)}</MenuItem>
            ))}
          </Select>

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

          <Box>
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 0.5 }}>
              {t('analytics.cameras')}
            </Typography>
            <Select
              multiple
              value={editing.cameras.map((c) => c.cameraId)}
              onChange={handleCamerasChange}
              size="small"
              displayEmpty
              renderValue={(selected) => selected.map(cameraName).join(', ') || t('analytics.selectCameras')}
              sx={{ maxWidth: 400, width: '100%' }}
            >
              {allCameras.map((c) => (
                <MenuItem key={c.cameraId} value={c.cameraId}>{c.name || c.cameraId}</MenuItem>
              ))}
            </Select>
          </Box>

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

          {error && (
            <Typography variant="caption" color="error">{error}</Typography>
          )}

          <Stack direction="row" spacing={1}>
            <Button type="submit" variant="outlined" size="small" disabled={busy}>
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
