import { useState } from 'react'
import { Alert, Box, CircularProgress, MenuItem, Select, Stack, Switch, Tooltip, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'
import { setDigitalInputArmMode } from '../api/deviceIoApi'
import type { DeviceArmMode, DigitalInputDto, RelayOutputDto } from '../dto/dtos'
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
// caption "Guard" — the three-position control that decides its arming. Three, not a switch: an
// explicit on or off is the operator's decision and outranks the camera (and the zone it stands
// in), while `Inherit` hands the input back to them. The caption beside it is what the mode
// actually resolves to, which is not the mode itself: `Inherit` under an armed camera is on guard.
function DigitalInputRow({
  cameraId, input, onChanged,
}: { cameraId: string; input: DigitalInputDto; onChanged: () => void }) {
  const { t } = useTranslation()
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const armed = input.armed
  const showGuard = input.alarmEnabled === true
  const mode = input.armMode ?? 'inherit'

  const apply = async (next: DeviceArmMode) => {
    setPending(true)
    setError(null)
    try {
      await setDigitalInputArmMode(cameraId, input.token, next)
      onChanged()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
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

  const guardSelect = (
    <Select
      size="small"
      value={mode}
      disabled={pending || !input.canControl}
      onChange={(event) => void apply(event.target.value as DeviceArmMode)}
      sx={{ minWidth: 104, fontSize: '0.75rem' }}
    >
      <MenuItem value="inherit">{t('objectSettings.inherit')}</MenuItem>
      <MenuItem value="armed">{t('objectSettings.armed')}</MenuItem>
      <MenuItem value="disarmed">{t('objectSettings.disarmed')}</MenuItem>
    </Select>
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
          {input.canControl ? guardSelect : (
            <Tooltip title={t('onvifSettings.deviceIoNoControl')}><span>{guardSelect}</span></Tooltip>
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
