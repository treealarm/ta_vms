import { useCallback, useEffect, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Checkbox,
  Chip,
  CircularProgress,
  FormControlLabel,
  IconButton,
  MenuItem,
  Select,
  Stack,
  Switch,
  Typography,
} from '@mui/material'
import ArrowUpwardIcon from '@mui/icons-material/ArrowUpward'
import ArrowDownwardIcon from '@mui/icons-material/ArrowDownward'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import { useTranslation } from 'react-i18next'
import { apiFetch } from '../api/apiFetch'
import type { AnalyticsDetectionSource, PtzTrackingPolicyDto } from '../dto/dtos'

type Props = {
  cameraId: string
}

// The kinds a detector can report, in the order they are offered. Same strings the overlay and
// analytics_watch.classes use.
const KINDS = ['person', 'face', 'vehicle', 'license_plate', 'animal'] as const
const SOURCES: AnalyticsDetectionSource[] = ['cv', 'onvif']

const EMPTY: PtzTrackingPolicyDto = { enabled: false, kinds: [], sources: [] }

export default function CameraTrackingSection({ cameraId }: Props) {
  const { t } = useTranslation()
  const [policy, setPolicy] = useState<PtzTrackingPolicyDto>(EMPTY)
  const [saved, setSaved] = useState<PtzTrackingPolicyDto>(EMPTY)
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const reload = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const data = await apiFetch<PtzTrackingPolicyDto>(
        `/api/ptz-tracking-policy/${encodeURIComponent(cameraId)}`)
      const loaded = data ?? EMPTY
      setPolicy(loaded)
      setSaved(loaded)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }, [cameraId])

  useEffect(() => { void reload() }, [reload])

  const dirty = JSON.stringify(policy) !== JSON.stringify(saved)

  const save = async () => {
    setBusy(true)
    setError(null)
    try {
      await apiFetch(`/api/ptz-tracking-policy/${encodeURIComponent(cameraId)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(policy),
      })
      setSaved(policy)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(false)
    }
  }

  const addKind = (kind: string) => {
    if (policy.kinds.includes(kind)) return
    setPolicy({ ...policy, kinds: [...policy.kinds, kind] })
  }

  const removeKind = (kind: string) =>
    setPolicy({ ...policy, kinds: policy.kinds.filter((k) => k !== kind) })

  const moveKind = (index: number, by: number) => {
    const next = [...policy.kinds]
    const to = index + by
    if (to < 0 || to >= next.length) return
    ;[next[index], next[to]] = [next[to], next[index]]
    setPolicy({ ...policy, kinds: next })
  }

  const toggleSource = (source: AnalyticsDetectionSource) =>
    setPolicy({
      ...policy,
      sources: policy.sources.includes(source)
        ? policy.sources.filter((s) => s !== source)
        : [...policy.sources, source],
    })

  const unused = KINDS.filter((k) => !policy.kinds.includes(k))

  if (loading)
    return <Box sx={{ py: 2 }}><CircularProgress size={20} /></Box>

  return (
    <Stack spacing={2}>
      {error && <Alert severity="error" onClose={() => setError(null)}>{error}</Alert>}

      <FormControlLabel
        control={
          <Switch
            checked={policy.enabled}
            onChange={(e) => setPolicy({ ...policy, enabled: e.target.checked })}
          />
        }
        label={t('tracking.selfGuidance')}
      />
      <Typography variant="caption" color="text.secondary">
        {t('tracking.selfGuidanceHint')}
      </Typography>

      {/* Heading and the control that adds to the list share a row: the panel is short and this
          section would otherwise not fit beside the camera's other settings. */}
      <Stack direction="row" spacing={1} alignItems="center" justifyContent="space-between">
        <Typography variant="subtitle2">{t('tracking.priorities')}</Typography>
        {unused.length > 0 && (
          <Select
            size="small"
            displayEmpty
            value=""
            onChange={(e) => addKind(e.target.value)}
            renderValue={() => t('tracking.addKind')}
            sx={{ minWidth: 140 }}
          >
            {unused.map((kind) => (
              <MenuItem key={kind} value={kind}>{t(`analytics.class.${kind}`, kind)}</MenuItem>
            ))}
          </Select>
        )}
      </Stack>
      <Typography variant="caption" color="text.secondary" sx={{ mt: -1 }}>
        {policy.kinds.length === 0 ? t('tracking.anyObject') : t('tracking.prioritiesHint')}
      </Typography>

      <Stack spacing={0.5}>
        {policy.kinds.map((kind, index) => (
          <Stack key={kind} direction="row" spacing={1} alignItems="center">
            <Chip size="small" label={index + 1} />
            <Typography sx={{ flex: 1 }}>{t(`analytics.class.${kind}`, kind)}</Typography>
            <IconButton
              size="small"
              disabled={index === 0}
              onClick={() => moveKind(index, -1)}
            >
              <ArrowUpwardIcon fontSize="inherit" />
            </IconButton>
            <IconButton
              size="small"
              disabled={index === policy.kinds.length - 1}
              onClick={() => moveKind(index, 1)}
            >
              <ArrowDownwardIcon fontSize="inherit" />
            </IconButton>
            <IconButton size="small" onClick={() => removeKind(kind)}>
              <DeleteOutlineIcon fontSize="inherit" />
            </IconButton>
          </Stack>
        ))}
      </Stack>

      <Typography variant="subtitle2">{t('tracking.sources')}</Typography>
      <Typography variant="caption" color="text.secondary" sx={{ mt: -1 }}>
        {policy.sources.length === 0 ? t('tracking.anySource') : t('tracking.sourcesHint')}
      </Typography>
      <Stack direction="row" spacing={2} sx={{ mt: -0.5 }}>
        {SOURCES.map((source) => (
          <FormControlLabel
            key={source}
            control={
              <Checkbox
                size="small"
                checked={policy.sources.includes(source)}
                onChange={() => toggleSource(source)}
              />
            }
            label={t(`analytics.source.${source}`, source)}
          />
        ))}
      </Stack>

      <Stack direction="row" spacing={1}>
        <Button variant="contained" size="small" disabled={!dirty || busy} onClick={() => void save()}>
          {t('common.save')}
        </Button>
        <Button size="small" disabled={!dirty || busy} onClick={() => setPolicy(saved)}>
          {t('common.cancel')}
        </Button>
      </Stack>
    </Stack>
  )
}
