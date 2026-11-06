import { useEffect, useState } from 'react'
import {
  Alert,
  Button,
  Card,
  CardContent,
  Chip,
  CircularProgress,
  Divider,
  MenuItem,
  Skeleton,
  Stack,
  Switch,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'
import { apiFetch } from '../api/apiFetch'
import type {
  DeviceArmMode,
  DeviceSettingsDto,
  DigitalInputDto,
  DigitalInputOptionsDto,
  IdleState,
  RelayMode,
  RelayOutputDto,
  RelayOutputOptionsDto,
} from '../dto/dtos'
import { setRelayOutputState, useRelayToggle } from '../hooks/useRelayToggle'
import { useDeviceIoLive } from '../hooks/useDeviceIoLive'

// ── Digital inputs ──────────────────────────────────────────────────────────

function DigitalInputRow({ cameraId, input, options, settings: saved, onSaved }: {
  cameraId: string
  input: DigitalInputDto
  options: DigitalInputOptionsDto | undefined
  settings: DeviceSettingsDto | undefined
  /** The idle state is part of the list itself; the read has to happen again after a write. */
  onSaved?: () => void
}) {
  const { t } = useTranslation()
  const [idleState, setIdleState] = useState<IdleState>(input.idleState ?? 'closed')
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saveOk, setSaveOk] = useState(false)
  const configurable = options?.idleStateConfigurable ?? false

  // Arming and alarm settings live in VmsCfg, not in the camera: a missing row is the default
  // (inherit the camera, no alarm), which is what the card starts from.
  const [armMode, setArmMode] = useState<DeviceArmMode>(saved?.armMode ?? 'inherit')
  const [alarmEnabled, setAlarmEnabled] = useState(saved?.alarmEnabled ?? false)
  const [triggerOn, setTriggerOn] = useState<IdleState>(saved?.triggerOn ?? 'closed')
  const [settingsSaving, setSettingsSaving] = useState(false)
  const [settingsError, setSettingsError] = useState<string | null>(null)
  const [settingsOk, setSettingsOk] = useState(false)

  useEffect(() => {
    setArmMode(saved?.armMode ?? 'inherit')
    setAlarmEnabled(saved?.alarmEnabled ?? false)
    setTriggerOn(saved?.triggerOn ?? 'closed')
  }, [saved])

  // Unknown polarity is a warning, not a disabled control: the setting is stored, the alarm simply
  // cannot be raised until the camera says which contact is idle.
  const polarityUnknown = input.idleState === null

  const handleSave = async () => {
    setSaving(true); setSaveError(null); setSaveOk(false)
    try {
      await apiFetch(`/api/DeviceIo/cameras/${encodeURIComponent(cameraId)}/digital-inputs/${encodeURIComponent(input.token)}/idle-state`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ idleState }),
      })
      setSaveOk(true)
      onSaved?.()
      setTimeout(() => setSaveOk(false), 3000)
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : String(e))
    } finally {
      setSaving(false)
    }
  }

  const handleSaveSettings = async () => {
    setSettingsSaving(true); setSettingsError(null); setSettingsOk(false)
    try {
      await apiFetch(`/api/DeviceIo/cameras/${encodeURIComponent(cameraId)}/settings`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          kind: 'digital_input',
          token: input.token,
          name: saved?.name ?? '',
          armMode,
          triggerOn: alarmEnabled ? triggerOn : null,
          alarmEnabled,
        }),
      })
      setSettingsOk(true)
      setTimeout(() => setSettingsOk(false), 3000)
    } catch (e) {
      setSettingsError(e instanceof Error ? e.message : String(e))
    } finally {
      setSettingsSaving(false)
    }
  }

  return (
    <Stack spacing={1}>
      <Stack direction="row" alignItems="center" spacing={2}>
        <Typography variant="body2" sx={{ minWidth: 100 }}>{saved?.name || input.token}</Typography>
        <Tooltip title={t('onvifSettings.deviceIoInputStateTip')}>
          {/* The camera's own logical state, from Device/Trigger events. A dash means it has not
              reported one yet — not that the input reads zero. */}
          <Chip
            size="small"
            variant="outlined"
            color={input.liveActive === null ? 'default' : input.liveActive ? 'success' : 'default'}
            label={input.liveActive === null ? '—' : input.liveActive ? '1' : '0'}
          />
        </Tooltip>
        <TextField
          select
          size="small"
          label={t('onvifSettings.deviceIoIdleStateLabel')}
          value={idleState}
          disabled={!configurable}
          onChange={(e) => setIdleState(e.target.value as IdleState)}
          sx={{ minWidth: 140 }}
        >
          <MenuItem value="open">{t('onvifSettings.deviceIoIdleOpen')}</MenuItem>
          <MenuItem value="closed">{t('onvifSettings.deviceIoIdleClosed')}</MenuItem>
        </TextField>
        <Button variant="contained" size="small" disabled={!configurable || saving} onClick={() => void handleSave()}>
          {saving ? <CircularProgress size={16} /> : t('onvifSettings.deviceIoSave')}
        </Button>
        {saveOk && <Typography variant="body2" color="success.main">✓</Typography>}
      </Stack>
      {!configurable && (
        <Alert severity="warning" sx={{ py: 0 }}>{t('onvifSettings.deviceIoIdleStateUnsupported')}</Alert>
      )}
      {polarityUnknown && (
        <Alert severity="warning" sx={{ py: 0 }}>{t('onvifSettings.deviceIoPolarityUnknown')}</Alert>
      )}

      <Stack direction="row" alignItems="center" spacing={2} flexWrap="wrap" useFlexGap>
        <TextField
          select
          size="small"
          label={t('onvifSettings.deviceIoArmModeLabel')}
          value={armMode}
          onChange={(e) => setArmMode(e.target.value as DeviceArmMode)}
          sx={{ minWidth: 160 }}
        >
          <MenuItem value="inherit">{t('onvifSettings.deviceIoArmInherit')}</MenuItem>
          <MenuItem value="armed">{t('onvifSettings.deviceIoArmed')}</MenuItem>
          <MenuItem value="disarmed">{t('onvifSettings.deviceIoDisarmed')}</MenuItem>
        </TextField>
        <Stack direction="row" alignItems="center" spacing={1}>
          <Switch
            checked={alarmEnabled}
            onChange={(e) => setAlarmEnabled(e.target.checked)}
            inputProps={{ 'aria-label': t('onvifSettings.deviceIoAlarmEnabled') }}
          />
          <Typography variant="body2">{t('onvifSettings.deviceIoAlarmEnabled')}</Typography>
        </Stack>
        <TextField
          select
          size="small"
          label={t('onvifSettings.deviceIoTriggerOn')}
          value={triggerOn}
          disabled={!alarmEnabled}
          onChange={(e) => setTriggerOn(e.target.value as IdleState)}
          sx={{ minWidth: 140 }}
        >
          <MenuItem value="open">{t('onvifSettings.deviceIoTriggerOpen')}</MenuItem>
          <MenuItem value="closed">{t('onvifSettings.deviceIoTriggerClosed')}</MenuItem>
        </TextField>
        <Button variant="contained" size="small" disabled={settingsSaving} onClick={() => void handleSaveSettings()}>
          {settingsSaving ? <CircularProgress size={16} /> : t('onvifSettings.deviceIoSave')}
        </Button>
        {settingsOk && <Typography variant="body2" color="success.main">✓</Typography>}
      </Stack>
      {settingsError && <Alert severity="error" onClose={() => setSettingsError(null)}>{settingsError}</Alert>}
      {saveError && <Alert severity="error" onClose={() => setSaveError(null)}>{saveError}</Alert>}
    </Stack>
  )
}

function DigitalInputsGroup({ cameraId, inputs, loaded, onSaved }: {
  cameraId: string
  inputs: DigitalInputDto[]
  loaded: boolean
  onSaved?: () => void
}) {
  const { t } = useTranslation()
  const [options, setOptions] = useState<DigitalInputOptionsDto[]>([])
  const [settings, setSettings] = useState<DeviceSettingsDto[]>([])

  // Options are advisory (they only gate the idle-state editor). Load them once and degrade to
  // "none" on failure, so an options error never hides the inputs list itself.
  useEffect(() => {
    let cancelled = false
    apiFetch<DigitalInputOptionsDto[]>(`/api/DeviceIo/cameras/${encodeURIComponent(cameraId)}/digital-inputs/options`)
      .then((o) => { if (!cancelled) setOptions(o) })
      .catch(() => { if (!cancelled) setOptions([]) })
    return () => { cancelled = true }
  }, [cameraId])

  // A part with no row is not an error: it keeps the defaults, which is what the row starts from.
  useEffect(() => {
    let cancelled = false
    apiFetch<DeviceSettingsDto[]>(`/api/DeviceIo/cameras/${encodeURIComponent(cameraId)}/settings`)
      .then((s) => { if (!cancelled) setSettings(s) })
      .catch(() => { if (!cancelled) setSettings([]) })
    return () => { cancelled = true }
  }, [cameraId])

  return (
    <Stack spacing={1.5}>
      <Typography variant="subtitle2">{t('onvifSettings.deviceIoSensorsGroup')}</Typography>
      {!loaded ? (
        <Skeleton variant="rounded" height={40} />
      ) : inputs.length === 0 ? (
        <Typography variant="body2" color="text.secondary">{t('onvifSettings.deviceIoNoSensors')}</Typography>
      ) : (
        <Stack spacing={2}>
          {inputs.map((i) => (
            <DigitalInputRow
              key={i.token}
              cameraId={cameraId}
              input={i}
              options={options.find((o) => o.token === i.token)}
              settings={settings.find((s) => s.kind === 'digital_input' && s.token === i.token)}
              onSaved={onSaved}
            />
          ))}
        </Stack>
      )}
    </Stack>
  )
}

// ── Relay outputs ────────────────────────────────────────────────────────────

function RelayOutputRow({ cameraId, relay, options, onSaved }: {
  cameraId: string
  relay: RelayOutputDto
  options: RelayOutputOptionsDto | undefined
  /** Mode, delay and idle state are part of the list itself; the read has to happen again. */
  onSaved?: () => void
}) {
  const { t } = useTranslation()
  const [mode, setMode] = useState<RelayMode>(relay.mode)
  const [idleState, setIdleState] = useState<IdleState>(relay.idleState)
  const [delayMs, setDelayMs] = useState(relay.delayMs)
  // Drive the optimistic auto-revert timer off the locally edited mode/delay (post-save, without a
  // refetch) rather than the original props, so a toggle after a mode/delay change reverts correctly.
  const { active, applyCommandedState } = useRelayToggle(mode, delayMs)
  const [toggling, setToggling] = useState(false)
  const [toggleError, setToggleError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saveOk, setSaveOk] = useState(false)

  const handleToggle = async () => {
    const next = !active
    setToggling(true); setToggleError(null)
    try {
      await setRelayOutputState(cameraId, relay.token, next)
      applyCommandedState(next)
    } catch (e) {
      setToggleError(e instanceof Error ? e.message : String(e))
    } finally {
      setToggling(false)
    }
  }

  const handleSave = async () => {
    setSaving(true); setSaveError(null); setSaveOk(false)
    try {
      await apiFetch(`/api/DeviceIo/cameras/${encodeURIComponent(cameraId)}/relay-outputs/${encodeURIComponent(relay.token)}/settings`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ mode, idleState, delayMs }),
      })
      setSaveOk(true)
      onSaved?.()
      setTimeout(() => setSaveOk(false), 3000)
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : String(e))
    } finally {
      setSaving(false)
    }
  }

  const min = options?.minDelayMs ?? 0
  const max = options?.maxDelayMs ?? 60_000
  // Always offer both modes, ignoring the camera's reported list. GetRelayOutputOptions.Mode is
  // spec'd as the *supported* modes, but some cameras (e.g. NVT1) appear to put just the *current*
  // mode there instead — so a single-entry list can't be trusted to mean "only this is supported".
  // Since pulse was dropped, monostable/bistable is the entire domain anyway, so restricting by that
  // list only risks hiding a mode the user can really use. Fixed order for a stable dropdown.
  const modes: RelayMode[] = ['monostable', 'bistable']

  return (
    <Card variant="outlined">
      <CardContent>
        <Stack spacing={1.5}>
          <Stack direction="row" alignItems="center" spacing={2}>
            {/* The switch is what was commanded; the chip is what the camera reports back. They
                differ until the camera's next event arrives, and for a monostable relay they differ
                again once it drops out on its own. */}
            <Switch checked={active} disabled={toggling} onChange={() => void handleToggle()} />
            <Typography variant="body2" sx={{ flex: 1 }}>{relay.token}</Typography>
            <Tooltip title={t('onvifSettings.deviceIoRelayStateTip')}>
              <Chip
                size="small"
                variant="outlined"
                color={relay.liveActive === true ? 'success' : 'default'}
                label={`${t('onvifSettings.deviceIoReportedState')}: ${
                  relay.liveActive === null
                    ? t('onvifSettings.deviceIoStateUnknown')
                    : relay.liveActive
                      ? t('onvifSettings.deviceIoActive')
                      : t('onvifSettings.deviceIoInactive')
                }`}
              />
            </Tooltip>
          </Stack>
          {toggleError && <Alert severity="error" onClose={() => setToggleError(null)}>{toggleError}</Alert>}
          <Stack direction="row" spacing={2} flexWrap="wrap" useFlexGap>
            <TextField
              select size="small" label={t('onvifSettings.deviceIoModeLabel')} value={mode}
              onChange={(e) => setMode(e.target.value as RelayMode)} sx={{ minWidth: 140 }}
            >
              {modes.map((m) => (
                <MenuItem key={m} value={m}>
                  {m === 'monostable' ? t('onvifSettings.deviceIoModeMonostable') : t('onvifSettings.deviceIoModeBistable')}
                </MenuItem>
              ))}
            </TextField>
            <TextField
              select size="small" label={t('onvifSettings.deviceIoIdleStateLabel')} value={idleState}
              onChange={(e) => setIdleState(e.target.value as IdleState)} sx={{ minWidth: 140 }}
            >
              <MenuItem value="open">{t('onvifSettings.deviceIoIdleOpen')}</MenuItem>
              <MenuItem value="closed">{t('onvifSettings.deviceIoIdleClosed')}</MenuItem>
            </TextField>
            <TextField
              type="number" size="small" label={t('onvifSettings.deviceIoDelayMs')} value={delayMs}
              disabled={mode !== 'monostable'}
              onChange={(e) => setDelayMs(Number(e.target.value))}
              slotProps={{ htmlInput: { min, max } }}
              sx={{ maxWidth: 140 }}
            />
          </Stack>
          <Stack direction="row" alignItems="center" spacing={1}>
            <Button variant="contained" size="small" disabled={saving} onClick={() => void handleSave()}>
              {saving ? <CircularProgress size={16} /> : t('onvifSettings.deviceIoSave')}
            </Button>
            {saveOk && <Typography variant="body2" color="success.main">✓</Typography>}
          </Stack>
          {saveError && <Alert severity="error" onClose={() => setSaveError(null)}>{saveError}</Alert>}
        </Stack>
      </CardContent>
    </Card>
  )
}

function RelayOutputsGroup({ cameraId, relays, loaded, onSaved }: {
  cameraId: string
  relays: RelayOutputDto[]
  loaded: boolean
  onSaved?: () => void
}) {
  const { t } = useTranslation()
  const [options, setOptions] = useState<RelayOutputOptionsDto[]>([])

  // Options are advisory (delay bounds; modes are shown fixed). Load them once and degrade to
  // "none" on failure, so an options error never hides the relay list itself.
  useEffect(() => {
    let cancelled = false
    apiFetch<RelayOutputOptionsDto[]>(`/api/DeviceIo/cameras/${encodeURIComponent(cameraId)}/relay-outputs/options`)
      .then((o) => { if (!cancelled) setOptions(o) })
      .catch(() => { if (!cancelled) setOptions([]) })
    return () => { cancelled = true }
  }, [cameraId])

  return (
    <Stack spacing={1.5}>
      <Typography variant="subtitle2">{t('onvifSettings.deviceIoRelaysGroup')}</Typography>
      {!loaded ? (
        <Skeleton variant="rounded" height={40} />
      ) : relays.length === 0 ? (
        <Typography variant="body2" color="text.secondary">{t('onvifSettings.deviceIoNoRelays')}</Typography>
      ) : (
        <Stack spacing={2}>
          {relays.map((r) => (
            <RelayOutputRow
              key={r.token}
              cameraId={cameraId}
              relay={r}
              options={options.find((o) => o.token === r.token)}
              onSaved={onSaved}
            />
          ))}
        </Stack>
      )}
    </Stack>
  )
}

// ── Section ───────────────────────────────────────────────────────────────

export default function DeviceIoSection({ cameraId }: { cameraId: string }) {
  // The lists are read once and their live states are pushed from the camera's Device/Trigger
  // events; only a settings write asks for the list again.
  const { relays, inputs, loaded, error, reload } = useDeviceIoLive(cameraId)

  return (
    <Stack spacing={3}>
      {error && <Alert severity="error">{error}</Alert>}
      <DigitalInputsGroup cameraId={cameraId} inputs={inputs} loaded={loaded} onSaved={reload} />
      <Divider />
      <RelayOutputsGroup cameraId={cameraId} relays={relays} loaded={loaded} onSaved={reload} />
    </Stack>
  )
}
