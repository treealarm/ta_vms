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

function DigitalInputRow({ cameraId, input, options }: {
  cameraId: string
  input: DigitalInputDto
  options: DigitalInputOptionsDto | undefined
}) {
  const { t } = useTranslation()
  const [idleState, setIdleState] = useState<IdleState>(input.idleState ?? 'closed')
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saveOk, setSaveOk] = useState(false)
  const configurable = options?.idleStateConfigurable ?? false

  const handleSave = async () => {
    setSaving(true); setSaveError(null); setSaveOk(false)
    try {
      await apiFetch(`/api/DeviceIo/cameras/${encodeURIComponent(cameraId)}/digital-inputs/${encodeURIComponent(input.token)}/idle-state`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ idleState }),
      })
      setSaveOk(true)
      setTimeout(() => setSaveOk(false), 3000)
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : String(e))
    } finally {
      setSaving(false)
    }
  }

  return (
    <Stack spacing={1}>
      <Stack direction="row" alignItems="center" spacing={2}>
        <Typography variant="body2" sx={{ minWidth: 100 }}>{input.token}</Typography>
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
      {saveError && <Alert severity="error" onClose={() => setSaveError(null)}>{saveError}</Alert>}
    </Stack>
  )
}

function DigitalInputsGroup({ cameraId, inputs, loaded }: {
  cameraId: string
  inputs: DigitalInputDto[]
  loaded: boolean
}) {
  const { t } = useTranslation()
  const [options, setOptions] = useState<DigitalInputOptionsDto[]>([])

  // Options are advisory (they only gate the idle-state editor). Load them once and degrade to
  // "none" on failure, so an options error never hides the inputs list itself.
  useEffect(() => {
    let cancelled = false
    apiFetch<DigitalInputOptionsDto[]>(`/api/DeviceIo/cameras/${encodeURIComponent(cameraId)}/digital-inputs/options`)
      .then((o) => { if (!cancelled) setOptions(o) })
      .catch(() => { if (!cancelled) setOptions([]) })
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
            />
          ))}
        </Stack>
      )}
    </Stack>
  )
}

// ── Relay outputs ────────────────────────────────────────────────────────────

function RelayOutputRow({ cameraId, relay, options }: {
  cameraId: string
  relay: RelayOutputDto
  options: RelayOutputOptionsDto | undefined
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

function RelayOutputsGroup({ cameraId, relays, loaded }: {
  cameraId: string
  relays: RelayOutputDto[]
  loaded: boolean
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
            />
          ))}
        </Stack>
      )}
    </Stack>
  )
}

// ── Section ───────────────────────────────────────────────────────────────

export default function DeviceIoSection({ cameraId }: { cameraId: string }) {
  // One poll for both lists, alive only while this tab is on screen. The lists come from the cache
  // VmsOnvif fills from the camera's own Device/Trigger events.
  const { relays, inputs, loaded, error } = useDeviceIoLive(cameraId)

  return (
    <Stack spacing={3}>
      {error && <Alert severity="error">{error}</Alert>}
      <DigitalInputsGroup cameraId={cameraId} inputs={inputs} loaded={loaded} />
      <Divider />
      <RelayOutputsGroup cameraId={cameraId} relays={relays} loaded={loaded} />
    </Stack>
  )
}
