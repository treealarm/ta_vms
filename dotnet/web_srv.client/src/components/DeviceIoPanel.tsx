import { useState } from 'react'
import { Alert, Box, Button, CircularProgress, MenuItem, Select, Stack, Switch, Tooltip, Typography } from '@mui/material'
import TimerOutlinedIcon from '@mui/icons-material/TimerOutlined'
import { useTranslation } from 'react-i18next'
import { setDigitalInputArmMode } from '../api/deviceIoApi'
import BypassUntilDialog from './control/BypassUntilDialog'
import { formatUntil } from './control/bypassTime'
import { guardState } from './control/guardState'
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
  const { t, i18n } = useTranslation()
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [bypassOpen, setBypassOpen] = useState(false)

  const armed = input.armed
  const showGuard = input.alarmEnabled === true
  const mode = input.armMode ?? 'inherit'

  const apply = async (next: DeviceArmMode, until?: string | null) => {
    setPending(true)
    setError(null)
    try {
      await setDigitalInputArmMode(cameraId, input.token, next, until)
      onChanged()
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
      onChanged()
    } finally {
      setPending(false)
    }
  }

  const state = guardState({
    armed: armed === true,
    source: input.armingSource,
    bypassed: input.bypassed,
    bypassUntil: input.bypassUntil,
    surface: 'device',
  })

  const guardLabel = armed === null
    ? t('onvifSettings.deviceIoGuardUnknown')
    : state.timed && input.bypassUntil
      ? t(state.labelKey, { time: formatUntil(input.bypassUntil, i18n.language) })
      : t(state.labelKey)

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
        <Stack direction="row" alignItems="center" spacing={1} useFlexGap flexWrap="wrap">
          <Typography variant="caption" color="text.secondary">
            {t('onvifSettings.deviceIoGuardLabel')}
          </Typography>
          {input.canControl ? guardSelect : (
            <Tooltip title={t('onvifSettings.deviceIoNoControl')}><span>{guardSelect}</span></Tooltip>
          )}
          <Button
            size="small"
            variant="outlined"
            startIcon={<TimerOutlinedIcon />}
            disabled={pending || !input.canControl}
            onClick={() => setBypassOpen(true)}
          >
            {t('control.bypassUntil')}
          </Button>
          <Typography
            variant="caption"
            color={armed === null ? 'text.secondary' : state.color}
            title={t('onvifSettings.deviceIoGuardHint')}
          >
            {guardLabel}
          </Typography>
        </Stack>
      )}
      {error && <Typography variant="caption" color="error.main">{error}</Typography>}
      <BypassUntilDialog
        open={bypassOpen}
        busy={pending}
        onClose={() => setBypassOpen(false)}
        onApply={(until) => {
          setBypassOpen(false)
          void apply('disarmed', until)
        }}
      />
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
