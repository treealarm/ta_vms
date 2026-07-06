import { useEffect, useState } from 'react'
import {
  Alert,
  Button,
  Card,
  CardContent,
  CircularProgress,
  Divider,
  MenuItem,
  Skeleton,
  Stack,
  Switch,
  TextField,
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

function DigitalInputsGroup({ cameraId }: { cameraId: string }) {
  const { t } = useTranslation()
  const [inputs, setInputs] = useState<DigitalInputDto[] | null>(null)
  const [options, setOptions] = useState<DigitalInputOptionsDto[]>([])
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    setError(null)
    Promise.all([
      apiFetch<DigitalInputDto[]>(`/api/DeviceIo/cameras/${encodeURIComponent(cameraId)}/digital-inputs`),
      apiFetch<DigitalInputOptionsDto[]>(`/api/DeviceIo/cameras/${encodeURIComponent(cameraId)}/digital-inputs/options`),
    ])
      .then(([i, o]) => { setInputs(i); setOptions(o) })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
  }, [cameraId])

  return (
    <Stack spacing={1.5}>
      <Typography variant="subtitle2">{t('onvifSettings.deviceIoSensorsGroup')}</Typography>
      {error && <Alert severity="error" onClose={() => setError(null)}>{error}</Alert>}
      {inputs === null ? (
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
            <Switch checked={active} disabled={toggling} onChange={() => void handleToggle()} />
            <Typography variant="body2" sx={{ flex: 1 }}>{relay.token}</Typography>
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

function RelayOutputsGroup({ cameraId }: { cameraId: string }) {
  const { t } = useTranslation()
  const [relays, setRelays] = useState<RelayOutputDto[] | null>(null)
  const [options, setOptions] = useState<RelayOutputOptionsDto[]>([])
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    setError(null)
    Promise.all([
      apiFetch<RelayOutputDto[]>(`/api/DeviceIo/cameras/${encodeURIComponent(cameraId)}/relay-outputs`),
      apiFetch<RelayOutputOptionsDto[]>(`/api/DeviceIo/cameras/${encodeURIComponent(cameraId)}/relay-outputs/options`),
    ])
      .then(([r, o]) => { setRelays(r); setOptions(o) })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
  }, [cameraId])

  return (
    <Stack spacing={1.5}>
      <Typography variant="subtitle2">{t('onvifSettings.deviceIoRelaysGroup')}</Typography>
      {error && <Alert severity="error" onClose={() => setError(null)}>{error}</Alert>}
      {relays === null ? (
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
  return (
    <Stack spacing={3}>
      <DigitalInputsGroup cameraId={cameraId} />
      <Divider />
      <RelayOutputsGroup cameraId={cameraId} />
    </Stack>
  )
}
