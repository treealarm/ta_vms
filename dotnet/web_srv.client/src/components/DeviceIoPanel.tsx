import { useEffect, useState } from 'react'
import { Alert, Box, CircularProgress, Stack, Switch, Tooltip, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'
import { setDigitalInputArmMode } from '../api/deviceIoApi'
import type { DigitalInputDto, RelayOutputDto } from '../dto/dtos'
import type { DeviceIoLiveState } from '../hooks/useDeviceIoLive'
import { setRelayOutputState, useRelayToggle } from '../hooks/useRelayToggle'

type Props = {
  cameraId: string
  /** Polled once by the camera tile, so the badge and this panel read the same snapshot. */
  live: DeviceIoLiveState & { reload: () => void }
}

// Relays only get a toggle here — mode/idle-state/delay configuration is admin-only, mirroring
// how PTZ presets are admin-only while the joystick lives in the monitor.
function RelayRow({ cameraId, relay, onChanged }: { cameraId: string; relay: RelayOutputDto; onChanged: () => void }) {
  const { t } = useTranslation()
  const { active } = useRelayToggle(relay.liveActive)
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const toggle = async () => {
    const next = !active
    setPending(true)
    setError(null)
    try {
      await setRelayOutputState(cameraId, relay.token, next)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setPending(false)
      // The server records the command before it answers, so this read carries the wish and the
      // `pending` flag that says the camera has not confirmed it yet.
      onChanged()
    }
  }

  // What the camera reports; while the command is unconfirmed the server answers with the wish
  // instead, and the caption says that is what it is.
  const reported = relay.livePending
    ? t('onvifSettings.deviceIoWaiting')
    : relay.liveActive === null
      ? t('onvifSettings.deviceIoStateUnknown')
      : relay.liveActive
        ? t('onvifSettings.deviceIoActive')
        : t('onvifSettings.deviceIoInactive')

  return (
    <Stack spacing={0.25}>
      <Stack direction="row" alignItems="center" spacing={1}>
        {relay.canControl ? (
          <Switch size="small" checked={active} disabled={pending} onChange={() => void toggle()} />
        ) : (
          <Tooltip title={t('onvifSettings.deviceIoNoControl')}>
            <span><Switch size="small" checked={active} disabled onChange={() => void toggle()} /></span>
          </Tooltip>
        )}
        <Typography variant="body2" sx={{ flex: 1, fontSize: '0.78rem' }}>{relay.token}</Typography>
        <Typography variant="caption" color={relay.liveActive === true ? 'success.main' : 'text.secondary'}>
          {reported}
        </Typography>
      </Stack>
      {error && <Typography variant="caption" color="error.main">{error}</Typography>}
    </Stack>
  )
}

// A sensor row on the camera card: the contact it reports, and — on a line of its own, under the
// caption "Guard" — the switch that arms it. Arming from the monitor is ordinary operator work;
// the switch is kept off the token line because beside the live 0/1 it read as "change what the
// sensor says". The switch is checked by what the arming resolves to, not by the configured mode:
// 'inherit' under an armed camera means on guard, and showing that as off would be a lie.
function DigitalInputRow({
  cameraId, input, onChanged,
}: { cameraId: string; input: DigitalInputDto; onChanged: () => void }) {
  const { t } = useTranslation()
  // The click answers at once; the next read replaces it, and a refusal drops it back to what the
  // server still says, so the switch never keeps a promise the server did not make.
  const [optimistic, setOptimistic] = useState<boolean | null>(null)
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (optimistic !== null && input.armed === optimistic) setOptimistic(null)
  }, [input.armed, optimistic])

  const armed = optimistic ?? input.armed
  const showGuard = input.alarmEnabled === true

  const toggle = async () => {
    const next = !(armed === true)
    setPending(true)
    setError(null)
    setOptimistic(next)
    try {
      await setDigitalInputArmMode(cameraId, input.token, next ? 'armed' : 'disarmed')
      onChanged()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
      setOptimistic(null)
      onChanged()
    } finally {
      setPending(false)
    }
  }

  const guardLabel = armed === null
    ? t('onvifSettings.deviceIoGuardUnknown')
    : input.armingSource === 'space'
      ? (armed ? t('onvifSettings.deviceIoGuardOnZone') : t('onvifSettings.deviceIoGuardOffZone'))
      : input.armingSource === 'camera'
        ? (armed ? t('onvifSettings.deviceIoGuardOnCamera') : t('onvifSettings.deviceIoGuardOffCamera'))
        : (armed ? t('onvifSettings.deviceIoGuardOn') : t('onvifSettings.deviceIoGuardOff'))

  const guardSwitch = (
    <Switch
      size="small"
      checked={armed === true}
      disabled={pending || !input.canControl}
      onChange={() => void toggle()}
    />
  )

  return (
    <Stack spacing={0.25}>
      <Stack direction="row" alignItems="center" spacing={1}>
        <Typography variant="body2" sx={{ flex: 1, fontSize: '0.78rem' }}>{input.token}</Typography>
        <Typography variant="caption" color="text.secondary">
          {input.idleState === 'open' && t('onvifSettings.deviceIoIdleOpen')}
          {input.idleState === 'closed' && t('onvifSettings.deviceIoIdleClosed')}
          {input.idleState == null && '—'}
        </Typography>
        {/* The logical state the camera reports; a dash means it has not reported one yet. */}
        <Typography
          variant="caption"
          sx={{ minWidth: 10, textAlign: 'right' }}
          color={input.liveActive === null ? 'text.secondary' : input.liveActive ? 'success.main' : 'text.primary'}
        >
          {input.liveActive === null ? '—' : input.liveActive ? '1' : '0'}
        </Typography>
      </Stack>
      {showGuard && (
        <Stack direction="row" alignItems="center" spacing={1}>
          <Typography variant="caption" color="text.secondary">
            {t('onvifSettings.deviceIoGuardLabel')}
          </Typography>
          {input.canControl ? guardSwitch : (
            <Tooltip title={t('onvifSettings.deviceIoNoControl')}><span>{guardSwitch}</span></Tooltip>
          )}
          <Typography
            variant="caption"
            color={armed === true ? 'success.main' : 'text.secondary'}
            title={t('onvifSettings.deviceIoGuardHint')}
          >
            {guardLabel}
          </Typography>
        </Stack>
      )}
      {error && <Typography variant="caption" color="error.main">{error}</Typography>}
    </Stack>
  )
}

export default function DeviceIoPanel({ cameraId, live }: Props) {
  const { t } = useTranslation()
  const { relays, inputs, loaded, error } = live

  return (
    <Box sx={{ position: 'relative', minWidth: 200, maxWidth: 260 }}>
      {error && (
        <Alert severity="error" sx={{ mb: 1, zIndex: 2, fontSize: '0.72rem', py: 0.25 }}>
          {error}
        </Alert>
      )}
      {!loaded ? (
        <Stack alignItems="center" py={1}><CircularProgress size={18} /></Stack>
      ) : (
        <Stack spacing={1.25}>
          {relays.length > 0 && (
            <Stack spacing={0.5}>
              <Typography variant="caption" color="text.secondary">{t('onvifSettings.deviceIoRelaysGroup')}</Typography>
              {relays.map((r) => <RelayRow key={r.token} cameraId={cameraId} relay={r} onChanged={live.reload} />)}
            </Stack>
          )}
          {inputs.length > 0 && (
            <Stack spacing={0.5}>
              <Typography variant="caption" color="text.secondary">{t('onvifSettings.deviceIoSensorsGroup')}</Typography>
              {inputs.map((i) => (
                <DigitalInputRow key={i.token} cameraId={cameraId} input={i} onChanged={live.reload} />
              ))}
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
