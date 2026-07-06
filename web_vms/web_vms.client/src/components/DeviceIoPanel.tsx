import { useEffect, useState } from 'react'
import { Alert, Box, CircularProgress, Stack, Switch, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'
import type { DigitalInputDto, RelayOutputDto } from '../dto/dtos'
import { setRelayOutputState, useRelayToggle } from '../hooks/useRelayToggle'

type Props = { cameraId: string }

// Relays only get a toggle here — mode/idle-state/delay configuration is admin-only, mirroring
// how PTZ presets are admin-only while the joystick lives in the monitor.
function RelayRow({ cameraId, relay }: { cameraId: string; relay: RelayOutputDto }) {
  const { active, applyCommandedState } = useRelayToggle(relay.mode, relay.delayMs)
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const toggle = async () => {
    const next = !active
    setPending(true)
    setError(null)
    try {
      await setRelayOutputState(cameraId, relay.token, next)
      applyCommandedState(next)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setPending(false)
    }
  }

  return (
    <Stack spacing={0.25}>
      <Stack direction="row" alignItems="center" spacing={1}>
        <Switch size="small" checked={active} disabled={pending} onChange={() => void toggle()} />
        <Typography variant="body2" sx={{ flex: 1, fontSize: '0.78rem' }}>{relay.token}</Typography>
      </Stack>
      {error && <Typography variant="caption" color="error.main">{error}</Typography>}
    </Stack>
  )
}

function DigitalInputRow({ input }: { input: DigitalInputDto }) {
  const { t } = useTranslation()
  return (
    <Stack direction="row" alignItems="center" spacing={1}>
      <Typography variant="body2" sx={{ flex: 1, fontSize: '0.78rem' }}>{input.token}</Typography>
      <Typography variant="caption" color="text.secondary">
        {input.idleState === 'open' && t('onvifSettings.deviceIoIdleOpen')}
        {input.idleState === 'closed' && t('onvifSettings.deviceIoIdleClosed')}
        {input.idleState == null && '—'}
      </Typography>
    </Stack>
  )
}

export default function DeviceIoPanel({ cameraId }: Props) {
  const { t } = useTranslation()
  const [relays, setRelays] = useState<RelayOutputDto[] | null>(null)
  const [inputs, setInputs] = useState<DigitalInputDto[] | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    setError(null)
    Promise.all([
      fetch(`/api/DeviceIo/cameras/${encodeURIComponent(cameraId)}/relay-outputs`)
        .then((r) => (r.ok ? (r.json() as Promise<RelayOutputDto[]>) : Promise.reject(new Error(t('onvifSettings.deviceIoNoRelays'))))),
      fetch(`/api/DeviceIo/cameras/${encodeURIComponent(cameraId)}/digital-inputs`)
        .then((r) => (r.ok ? (r.json() as Promise<DigitalInputDto[]>) : Promise.reject(new Error(t('onvifSettings.deviceIoNoSensors'))))),
    ])
      .then(([r, i]) => { if (!cancelled) { setRelays(r); setInputs(i) } })
      .catch((e: unknown) => { if (!cancelled) setError(e instanceof Error ? e.message : String(e)) })
    return () => { cancelled = true }
  }, [cameraId, t])

  const loading = relays === null || inputs === null

  return (
    <Box sx={{ position: 'relative', minWidth: 200, maxWidth: 260 }}>
      {error && (
        <Alert
          severity="error"
          onClose={() => setError(null)}
          sx={{ mb: 1, zIndex: 2, fontSize: '0.72rem', py: 0.25 }}
        >
          {error}
        </Alert>
      )}
      {loading ? (
        <Stack alignItems="center" py={1}><CircularProgress size={18} /></Stack>
      ) : (
        <Stack spacing={1.25}>
          {relays.length > 0 && (
            <Stack spacing={0.5}>
              <Typography variant="caption" color="text.secondary">{t('onvifSettings.deviceIoRelaysGroup')}</Typography>
              {relays.map((r) => <RelayRow key={r.token} cameraId={cameraId} relay={r} />)}
            </Stack>
          )}
          {inputs.length > 0 && (
            <Stack spacing={0.5}>
              <Typography variant="caption" color="text.secondary">{t('onvifSettings.deviceIoSensorsGroup')}</Typography>
              {inputs.map((i) => <DigitalInputRow key={i.token} input={i} />)}
            </Stack>
          )}
          {relays.length === 0 && inputs.length === 0 && (
            <Typography variant="caption" color="text.secondary">{t('onvifSettings.deviceIoNoRelays')}</Typography>
          )}
        </Stack>
      )}
    </Box>
  )
}
