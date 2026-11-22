import { useCallback, useEffect, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  CircularProgress,
  Divider,
  IconButton,
  MenuItem,
  Skeleton,
  Stack,
  Switch,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import RefreshIcon from '@mui/icons-material/Refresh'
import SaveIcon from '@mui/icons-material/Save'
import { useTranslation } from 'react-i18next'
import { apiFetch } from '../api/apiFetch'
import { errorText } from '../api/requestJson'
import { renameCameraPart } from '../api/controlApi'
import type {
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
import ObjectSettingsCard from './objects/ObjectSettingsCard'

type PartKind = 'digital_input' | 'relay'

const partKey = (kind: PartKind, token: string) => `${kind}:${token}`

/** The name is the part's identity: saved on its own, never bundled with its behaviour. */
function PartNameField({ cameraId, kind, token, savedName, onSaved }: {
  cameraId: string
  kind: PartKind
  token: string
  savedName: string
  onSaved: (name: string) => void
}) {
  const { t } = useTranslation()
  const [name, setName] = useState(savedName)
  const [saved, setSaved] = useState(savedName)
  const [saving, setSaving] = useState(false)
  const [ok, setOk] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    setName(savedName)
    setSaved(savedName)
  }, [savedName])

  const trimmed = name.trim()
  const dirty = trimmed !== saved

  const save = async () => {
    if (!trimmed) {
      setError(t('onvifSettings.deviceIoNameEmpty'))
      return
    }
    setSaving(true); setError(null); setOk(false)
    try {
      await renameCameraPart(cameraId, { kind, token, name: trimmed })
      setName(trimmed)
      setSaved(trimmed)
      setOk(true)
      onSaved(trimmed)
      setTimeout(() => setOk(false), 3000)
    } catch (e) {
      setError(errorText(e))
    } finally {
      setSaving(false)
    }
  }

  return (
    <Stack spacing={0.5}>
      <Stack direction="row" spacing={1} alignItems="flex-start">
        <TextField
          size="small"
          label={t('onvifSettings.deviceIoNameLabel')}
          value={name}
          placeholder={token}
          helperText={t('onvifSettings.deviceIoNameHelper')}
          onChange={(e) => setName(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault()
              void save()
            }
          }}
          sx={{ maxWidth: 360, flex: 1 }}
        />
        <Tooltip title={t('onvifSettings.deviceIoNameSave')}>
          <span>
            <IconButton
              size="small"
              aria-label={t('onvifSettings.deviceIoNameSave')}
              disabled={saving || !dirty || !trimmed}
              onClick={() => void save()}
            >
              {saving ? <CircularProgress size={16} /> : <SaveIcon fontSize="small" />}
            </IconButton>
          </span>
        </Tooltip>
        {ok && <Typography variant="body2" color="success.main">✓</Typography>}
      </Stack>
      {error && <Alert severity="error" sx={{ py: 0 }} onClose={() => setError(null)}>{error}</Alert>}
    </Stack>
  )
}

/** Whether the administrator has named the part yet, and whether the camera is answering at all. */
function PartStatus({ configured, problem }: { configured: boolean; problem: boolean }) {
  const { t } = useTranslation()
  if (problem)
    return <Chip size="small" variant="outlined" color="warning"
      label={t('onvifSettings.deviceIoStatusProblem')} />
  return (
    <Chip
      size="small"
      variant="outlined"
      color={configured ? 'success' : 'default'}
      label={configured
        ? t('onvifSettings.deviceIoStatusConfigured')
        : t('onvifSettings.deviceIoStatusUnconfigured')}
    />
  )
}

// ── Digital inputs ──────────────────────────────────────────────────────────

function DigitalInputRow({
  cameraId, input, options, settings, problem, refreshKey, onNameSaved, onDirtyChange,
}: {
  cameraId: string
  input: DigitalInputDto
  options: DigitalInputOptionsDto | undefined
  settings: DeviceSettingsDto | undefined
  problem: boolean
  refreshKey: number
  onNameSaved: (name: string) => void
  onDirtyChange: (dirty: boolean) => void
}) {
  const { t } = useTranslation()
  const [idleState, setIdleState] = useState<IdleState>(input.idleState ?? 'closed')
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saveOk, setSaveOk] = useState(false)
  const [displayName, setDisplayName] = useState(settings?.name || input.token)
  const configurable = options?.idleStateConfigurable ?? false

  useEffect(() => setDisplayName(settings?.name || input.token), [settings?.name, input.token])

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
        <Typography variant="body2" sx={{ minWidth: 100 }}>{displayName}</Typography>
        <PartStatus configured={!!settings?.name} problem={problem} />
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
      </Stack>

      <PartNameField
        cameraId={cameraId}
        kind="digital_input"
        token={input.token}
        savedName={settings?.name ?? ''}
        onSaved={(name) => {
          setDisplayName(name)
          onNameSaved(name)
        }}
      />

      <Typography variant="caption" color="text.secondary">
        {t('onvifSettings.deviceIoHardwareGroup')}
      </Typography>
      <Stack direction="row" alignItems="center" spacing={2}>
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

      {/* What the contact means. The arming mode is the object's own setting and is edited where
          the object is selected, not on the camera's hardware page. */}
      <Typography variant="caption" color="text.secondary">
        {t('onvifSettings.deviceIoBehaviourGroup')}
      </Typography>
      <ObjectSettingsCard
        cameraId={cameraId}
        partKind="digital_input"
        partToken={input.token}
        showArming={false}
        refreshKey={refreshKey}
        onDirtyChange={onDirtyChange}
      />
      {saveError && <Alert severity="error" onClose={() => setSaveError(null)}>{saveError}</Alert>}
    </Stack>
  )
}

function DigitalInputsGroup({
  cameraId, inputs, loaded, settings, problem, refreshKeyFor, onNameSaved, onDirtyChange,
}: {
  cameraId: string
  inputs: DigitalInputDto[]
  loaded: boolean
  settings: DeviceSettingsDto[]
  problem: boolean
  refreshKeyFor: (kind: PartKind, token: string) => number
  onNameSaved: (kind: PartKind, token: string, wasConfigured: boolean) => void
  onDirtyChange: (kind: PartKind, token: string, dirty: boolean) => void
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
          {inputs.map((i) => {
            const saved = settings.find((s) => s.kind === 'digital_input' && s.token === i.token)
            return (
              <DigitalInputRow
                key={i.token}
                cameraId={cameraId}
                input={i}
                options={options.find((o) => o.token === i.token)}
                settings={saved}
                problem={problem}
                refreshKey={refreshKeyFor('digital_input', i.token)}
                onNameSaved={() => onNameSaved('digital_input', i.token, !!saved?.name)}
                onDirtyChange={(dirty) => onDirtyChange('digital_input', i.token, dirty)}
              />
            )
          })}
        </Stack>
      )}
    </Stack>
  )
}

// ── Relay outputs ────────────────────────────────────────────────────────────

function RelayOutputRow({
  cameraId, relay, options, settings, problem, onNameSaved, onSaved,
}: {
  cameraId: string
  relay: RelayOutputDto
  options: RelayOutputOptionsDto | undefined
  settings: DeviceSettingsDto | undefined
  problem: boolean
  onNameSaved: (name: string) => void
  onSaved: () => void
}) {
  const { t } = useTranslation()
  const [mode, setMode] = useState<RelayMode>(relay.mode)
  const [idleState, setIdleState] = useState<IdleState>(relay.idleState)
  const [delayMs, setDelayMs] = useState(relay.delayMs)
  const [displayName, setDisplayName] = useState(settings?.name || relay.token)
  // The server answers with the operator's wish until the camera confirms it, so the switch needs
  // no guess of its own here.
  const { active } = useRelayToggle(relay.liveActive)
  const [toggling, setToggling] = useState(false)
  const [toggleError, setToggleError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saveOk, setSaveOk] = useState(false)

  useEffect(() => setDisplayName(settings?.name || relay.token), [settings?.name, relay.token])

  const handleToggle = async () => {
    const next = !active
    setToggling(true); setToggleError(null)
    try {
      await setRelayOutputState(cameraId, relay.token, next)
      // Re-read the list: the command is recorded before it returns, so the read carries the wish
      // and the `pending` flag that says the camera has not confirmed it yet.
      onSaved()
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
      onSaved()
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
            {/* One value on screen: until the camera confirms the command the switch shows the
                wish, and the caption says so; afterwards the chip is the camera's own report. */}
            <Switch checked={active} disabled={toggling} onChange={() => void handleToggle()} />
            <Typography variant="body2" sx={{ flex: 1 }}>{displayName}</Typography>
            <PartStatus configured={!!settings?.name} problem={problem} />
            {relay.livePending ? (
              <Typography variant="caption" color="text.secondary">
                {t('onvifSettings.deviceIoWaiting')}
              </Typography>
            ) : (
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
            )}
          </Stack>

          <PartNameField
            cameraId={cameraId}
            kind="relay"
            token={relay.token}
            savedName={settings?.name ?? ''}
            onSaved={(name) => {
              setDisplayName(name)
              onNameSaved(name)
            }}
          />

          {toggleError && <Alert severity="error" onClose={() => setToggleError(null)}>{toggleError}</Alert>}
          <Typography variant="caption" color="text.secondary">
            {t('onvifSettings.deviceIoHardwareGroup')}
          </Typography>
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

function RelayOutputsGroup({
  cameraId, relays, loaded, settings, problem, onNameSaved, onSaved,
}: {
  cameraId: string
  relays: RelayOutputDto[]
  loaded: boolean
  settings: DeviceSettingsDto[]
  problem: boolean
  onNameSaved: (kind: PartKind, token: string, wasConfigured: boolean) => void
  onSaved: () => void
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
          {relays.map((r) => {
            const saved = settings.find((s) => s.kind === 'relay' && s.token === r.token)
            return (
              <RelayOutputRow
                key={r.token}
                cameraId={cameraId}
                relay={r}
                options={options.find((o) => o.token === r.token)}
                settings={saved}
                problem={problem}
                onNameSaved={() => onNameSaved('relay', r.token, !!saved?.name)}
                onSaved={onSaved}
              />
            )
          })}
        </Stack>
      )}
    </Stack>
  )
}

// ── Section ───────────────────────────────────────────────────────────────

export default function DeviceIoSection({ cameraId }: { cameraId: string }) {
  const { t, i18n } = useTranslation()
  // The lists are read once and their live states are pushed from the camera's Device/Trigger
  // events; only a settings write asks for the list again.
  const { relays, inputs, loaded, error, reload } = useDeviceIoLive(cameraId)
  const [settings, setSettings] = useState<DeviceSettingsDto[]>([])
  const [settingsLoaded, setSettingsLoaded] = useState(false)
  const [settingsError, setSettingsError] = useState<string | null>(null)
  const [cardKeys, setCardKeys] = useState<Record<string, number>>({})
  const [dirtyCards, setDirtyCards] = useState<Record<string, boolean>>({})
  const [updatedAt, setUpdatedAt] = useState<Date | null>(null)
  const [refreshing, setRefreshing] = useState(false)

  const loadSettings = useCallback(async () => {
    try {
      const next = await apiFetch<DeviceSettingsDto[]>(
        `/api/DeviceIo/cameras/${encodeURIComponent(cameraId)}/settings`)
      setSettings(next)
      setSettingsError(null)
      setUpdatedAt(new Date())
    } catch (e) {
      setSettingsError(errorText(e))
    } finally {
      setSettingsLoaded(true)
    }
  }, [cameraId])

  useEffect(() => {
    setSettings([])
    setSettingsLoaded(false)
    void loadSettings()
  }, [loadSettings])

  const refresh = async () => {
    setRefreshing(true)
    reload()
    await loadSettings()
    setRefreshing(false)
  }

  const isConfigured = (kind: PartKind, token: string) =>
    !!settings.find((s) => s.kind === kind && s.token === token)?.name

  const configuredCount =
    inputs.filter((i) => isConfigured('digital_input', i.token)).length +
    relays.filter((r) => isConfigured('relay', r.token)).length
  const total = inputs.length + relays.length

  // A first name makes the part "configured", so the card re-reads and its controls enable. A
  // rename of an already configured part must not throw away the card's unsaved behaviour edits.
  const onNameSaved = (kind: PartKind, token: string, wasConfigured: boolean) => {
    const key = partKey(kind, token)
    if (!wasConfigured || !dirtyCards[key]) {
      setCardKeys((prev) => ({ ...prev, [key]: (prev[key] ?? 0) + 1 }))
    }
    void loadSettings()
  }

  const onDirtyChange = (kind: PartKind, token: string, dirty: boolean) => {
    const key = partKey(kind, token)
    setDirtyCards((prev) => (prev[key] === dirty ? prev : { ...prev, [key]: dirty }))
  }

  const refreshKeyFor = (kind: PartKind, token: string) => cardKeys[partKey(kind, token)] ?? 0
  const noParts = loaded && settingsLoaded && inputs.length === 0 && relays.length === 0

  return (
    <Stack spacing={2}>
      <Stack direction="row" alignItems="center" spacing={1.5}>
        <Typography variant="subtitle1">{t('onvifSettings.deviceIoTitle')}</Typography>
        <Typography variant="caption" color="text.secondary">
          {t('onvifSettings.deviceIoConfiguredCount', { configured: configuredCount, total })}
        </Typography>
        <Box sx={{ flex: 1 }} />
        {updatedAt && (
          <Typography variant="caption" color="text.secondary">
            {t('onvifSettings.deviceIoUpdated', { time: updatedAt.toLocaleTimeString(i18n.language) })}
          </Typography>
        )}
        <Tooltip title={t('onvifSettings.deviceIoRefresh')}>
          <span>
            <IconButton
              size="small"
              aria-label={t('onvifSettings.deviceIoRefresh')}
              disabled={refreshing}
              onClick={() => void refresh()}
            >
              {refreshing ? <CircularProgress size={18} /> : <RefreshIcon fontSize="small" />}
            </IconButton>
          </span>
        </Tooltip>
      </Stack>

      {error && <Alert severity="error">{t('onvifSettings.deviceIoOffline')}</Alert>}
      {settingsError && <Alert severity="error">{settingsError}</Alert>}
      {noParts ? (
        <Typography variant="body2" color="text.secondary">
          {t('onvifSettings.deviceIoNoParts')}
        </Typography>
      ) : (
        <>
          <DigitalInputsGroup
            cameraId={cameraId}
            inputs={inputs}
            loaded={loaded}
            settings={settings}
            problem={!!error}
            refreshKeyFor={refreshKeyFor}
            onNameSaved={onNameSaved}
            onDirtyChange={onDirtyChange}
          />
          <Divider />
          <RelayOutputsGroup
            cameraId={cameraId}
            relays={relays}
            loaded={loaded}
            settings={settings}
            problem={!!error}
            onNameSaved={onNameSaved}
            onSaved={reload}
          />
        </>
      )}
    </Stack>
  )
}
