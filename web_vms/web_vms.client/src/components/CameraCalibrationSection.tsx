import { useCallback, useEffect, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  ButtonGroup,
  CircularProgress,
  Stack,
  Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'
import { apiFetch } from '../api/apiFetch'
import type { PtzCalibrationDto } from '../dto/dtos'

type Props = {
  cameraId: string
  hasTelemetry: boolean
}

type WizardStep = 'idle' | 'pan' | 'tilt'

const DIRECTIONS = ['left', 'right', 'up', 'down'] as const

function errorMessage(err: unknown, fallback: string): string {
  if (!(err instanceof Error) || !err.message)
    return fallback
  try {
    const parsed = JSON.parse(err.message) as { error?: string }
    if (typeof parsed.error === 'string' && parsed.error)
      return parsed.error
  } catch {
    // The body was not the calibration DTO — show a short line, not a raw dump.
  }
  const text = err.message.trim()
  if (text.startsWith('{') || text.startsWith('['))
    return fallback
  return text
}

export default function CameraCalibrationSection({ cameraId, hasTelemetry }: Props) {
  const { t } = useTranslation()
  const [calibration, setCalibration] = useState<PtzCalibrationDto | null>(null)
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [mode, setMode] = useState<'auto' | 'wizard'>('auto')
  const [wizardSessionId, setWizardSessionId] = useState<string | null>(null)
  const [wizardStep, setWizardStep] = useState<WizardStep>('idle')

  const reload = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const data = await apiFetch<PtzCalibrationDto>(`/api/ptz-calibration/${encodeURIComponent(cameraId)}`)
      setCalibration(data)
    } catch {
      setCalibration(null)
    } finally {
      setLoading(false)
    }
  }, [cameraId])

  useEffect(() => { void reload() }, [reload])

  const runAuto = async () => {
    setBusy(true)
    setError(null)
    try {
      const result = await apiFetch<{ success: boolean; error?: string; calibration?: PtzCalibrationDto }>(
        `/api/ptz-calibration/${encodeURIComponent(cameraId)}/auto`,
        { method: 'POST' },
      )
      setCalibration(result.calibration ?? null)
    } catch (e) {
      setError(errorMessage(e, t('ptzCalibration.failed')))
    } finally {
      setBusy(false)
    }
  }

  const beginWizard = async () => {
    setBusy(true)
    setError(null)
    try {
      const result = await apiFetch<{ sessionId: string }>(
        `/api/ptz-calibration/${encodeURIComponent(cameraId)}/wizard/begin`,
        { method: 'POST' },
      )
      setWizardSessionId(result.sessionId)
      setWizardStep('pan')
    } catch (e) {
      setError(errorMessage(e, t('ptzCalibration.failed')))
    } finally {
      setBusy(false)
    }
  }

  // One test pulse, on demand. Repeatable: a single 350 ms move is easy to miss, pressing again
  // moves further, and the opposite button puts the camera back — so the operator can rehearse
  // until they are sure which way the picture went, instead of being asked about a move that has
  // already happened.
  const nudge = async (axis: 'pan' | 'tilt', positive: boolean) => {
    if (!wizardSessionId) return
    setBusy(true)
    setError(null)
    try {
      await apiFetch(`/api/ptz-calibration/wizard/${wizardSessionId}/nudge`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ cameraId, axis, positive }),
      })
    } catch (e) {
      setError(errorMessage(e, t('ptzCalibration.failed')))
    } finally {
      setBusy(false)
    }
  }

  const answerWizard = async (direction: string) => {
    if (!wizardSessionId) return
    setBusy(true)
    setError(null)
    try {
      if (wizardStep === 'pan') {
        await apiFetch(`/api/ptz-calibration/wizard/${wizardSessionId}/pan`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ cameraId, direction }),
        })
        setWizardStep('tilt')
        return
      }

      const result = await apiFetch<{ success: boolean; error?: string; calibration?: PtzCalibrationDto }>(
        `/api/ptz-calibration/wizard/${wizardSessionId}/tilt`,
        {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ cameraId, direction }),
        },
      )
      setCalibration(result.calibration ?? null)
      setWizardSessionId(null)
      setWizardStep('idle')
    } catch (e) {
      setError(errorMessage(e, t('ptzCalibration.failed')))
    } finally {
      setBusy(false)
    }
  }

  const clearCalibration = async () => {
    setBusy(true)
    setError(null)
    try {
      await apiFetch(`/api/ptz-calibration/${encodeURIComponent(cameraId)}`, { method: 'DELETE' })
      setCalibration(null)
      setWizardSessionId(null)
      setWizardStep('idle')
    } catch (e) {
      setError(errorMessage(e, t('ptzCalibration.failed')))
    } finally {
      setBusy(false)
    }
  }

  if (!hasTelemetry) {
    return (
      <Alert severity="info">{t('ptzCalibration.requiresTelemetry')}</Alert>
    )
  }

  if (loading) {
    return <CircularProgress size={24} />
  }

  return (
    <Stack spacing={2}>
      <Typography variant="body2" color="text.secondary">
        {t('ptzCalibration.intro')}
      </Typography>

      {calibration && (
        <Box sx={{ p: 1.5, border: 1, borderColor: 'divider', borderRadius: 1 }}>
          <Typography variant="subtitle2">{t('ptzCalibration.current')}</Typography>
          <Typography variant="caption" component="div" color="text.secondary">
            {t('ptzCalibration.method')}: {calibration.method}
            {calibration.calibratedAt ? ` · ${new Date(calibration.calibratedAt).toLocaleString()}` : ''}
          </Typography>
          <Typography variant="body2" sx={{ mt: 1, fontFamily: 'monospace', fontSize: '0.8rem' }}>
            pan+ → ({calibration.panDx.toFixed(2)}, {calibration.panDy.toFixed(2)})
            <br />
            tilt+ → ({calibration.tiltDx.toFixed(2)}, {calibration.tiltDy.toFixed(2)})
            <br />
            {t('ptzCalibration.confidence')}: {(calibration.confidence * 100).toFixed(0)}%
          </Typography>
        </Box>
      )}

      <ButtonGroup size="small" disabled={busy}>
        <Button
          variant={mode === 'auto' ? 'contained' : 'outlined'}
          onClick={() => setMode('auto')}
        >
          {t('ptzCalibration.modeAuto')}
        </Button>
        <Button
          variant={mode === 'wizard' ? 'contained' : 'outlined'}
          onClick={() => setMode('wizard')}
        >
          {t('ptzCalibration.modeWizard')}
        </Button>
      </ButtonGroup>

      {mode === 'auto' && (
        <Button variant="contained" disabled={busy} onClick={() => void runAuto()}>
          {busy ? <CircularProgress size={18} color="inherit" /> : t('ptzCalibration.runAuto')}
        </Button>
      )}

      {mode === 'wizard' && wizardStep === 'idle' && (
        <Button variant="contained" disabled={busy} onClick={() => void beginWizard()}>
          {busy ? <CircularProgress size={18} color="inherit" /> : t('ptzCalibration.startWizard')}
        </Button>
      )}

      {mode === 'wizard' && wizardStep !== 'idle' && (
        <Stack spacing={1}>
          <Typography variant="body2">
            {wizardStep === 'pan'
              ? t('ptzCalibration.nudgePan')
              : t('ptzCalibration.nudgeTilt')}
          </Typography>
          <ButtonGroup size="small" disabled={busy}>
            <Button onClick={() => void nudge(wizardStep === 'pan' ? 'pan' : 'tilt', false)}>
              {wizardStep === 'pan' ? 'pan −' : 'tilt −'}
            </Button>
            <Button onClick={() => void nudge(wizardStep === 'pan' ? 'pan' : 'tilt', true)}>
              {wizardStep === 'pan' ? 'pan +' : 'tilt +'}
            </Button>
          </ButtonGroup>
          <Typography variant="body2">
            {wizardStep === 'pan'
              ? t('ptzCalibration.askPan')
              : t('ptzCalibration.askTilt')}
          </Typography>
          <Stack direction="row" flexWrap="wrap" gap={1}>
            {DIRECTIONS.map((dir) => (
              <Button
                key={dir}
                size="small"
                variant="outlined"
                disabled={busy}
                onClick={() => void answerWizard(dir)}
              >
                {t(`ptzCalibration.direction.${dir}`)}
              </Button>
            ))}
          </Stack>
        </Stack>
      )}

      {calibration && (
        <Button color="warning" disabled={busy} onClick={() => void clearCalibration()}>
          {t('ptzCalibration.clear')}
        </Button>
      )}

      {error && <Alert severity="error">{error}</Alert>}
      <Alert severity="info">{t('ptzCalibration.staticSceneHint')}</Alert>
    </Stack>
  )
}
