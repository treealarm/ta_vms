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
import { useAppSelector } from '../store'
import type { AnalyticsDetectionSource, PtzCalibrationDto, PtzTrackingPolicyDto } from '../dto/dtos'

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
  const camerasById = useAppSelector((state) => state.live.cameraStatesById)
  const [policy, setPolicy] = useState<PtzTrackingPolicyDto>(EMPTY)
  const [calibration, setCalibration] = useState<PtzCalibrationDto | null>(null)
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

      // Whether the camera has been calibrated changes what switching this on will do, so it is
      // read here rather than left for the operator to go and check on another tab.
      try {
        setCalibration(await apiFetch<PtzCalibrationDto>(
          `/api/ptz-calibration/${encodeURIComponent(cameraId)}`))
      } catch {
        // No calibration row is the ordinary case, and the endpoint says so by failing. Not knowing
        // is reported the same way as not having one: the hint below is written to be true either
        // way.
        setCalibration(null)
      }
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
        {/* The same switch means something different for a slave: it starts aiming at another
            camera's objects, not looking for its own. Saying which master, here, is what keeps the
            operator from wondering why the camera never reacts to its own picture. */}
        {policy.masterCameraId
          ? t('tracking.selfGuidanceSlaveHint', {
              master: camerasById[policy.masterCameraId]?.name || policy.masterCameraId,
            })
          : t('tracking.selfGuidanceHint')}
      </Typography>

      {/* A slave is aimed by reading the bottom of a box as the object's contact with the ground.
          A face box has no such edge — its bottom is a chin — so preferring faces on a slave sends
          it nowhere useful, and alternating between a face and its body swings it between zones.
          Said here because the priority list is shared with self-guidance, where preferring a face
          is exactly right. */}
      {policy.masterCameraId
        && policy.kinds.some((k) => k === 'face' || k === 'license_plate') && (
        <Alert severity="warning" sx={{ py: 0.5 }}>
          <Typography variant="caption">{t('tracking.slaveNeedsWholeObjects')}</Typography>
        </Alert>
      )}

      {/* Calibration is not a prerequisite and saying so plainly matters: an uncalibrated camera
          tracks perfectly well as long as it is hung the usual way, because the loop falls back to
          assuming that. What it cannot survive is an inverted or swapped axis — then it drives away
          from the object rather than towards it, which is the one symptom worth naming here, since
          it looks like a broken feature rather than a missing setting. A slave is aimed through its
          survey and never uses this at all. */}
      {!policy.masterCameraId && (
        <Alert severity={calibration ? 'success' : 'info'} sx={{ py: 0.5 }}>
          <Typography variant="caption">
            {calibration
              ? t('tracking.calibrated', {
                  method: t(`ptzCalibration.method_${calibration.method}`, calibration.method),
                })
              : t('tracking.uncalibrated')}
          </Typography>
        </Alert>
      )}

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
