import { useEffect, useMemo, useState } from 'react'
import {
  Alert, Box, Button, Divider, FormControlLabel, IconButton, List, ListItemButton, ListItemText,
  MenuItem, Stack, Switch, TextField, Tooltip, Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import DeleteIcon from '@mui/icons-material/Delete'
import RefreshIcon from '@mui/icons-material/Refresh'
import { useTranslation } from 'react-i18next'
import type { EventSubscriptionDto, SubscriptionActionDto } from '../dto/dtos'
import { enc, errorText, jsonRequest, requestJson } from '../api/requestJson'
import DetailToolbar from './settings/DetailToolbar'
import MasterDetailLayout from './settings/MasterDetailLayout'

/**
 * Admin → Event routing: what the router in VmsSingleton executes when a journal event matches.
 * The panel edits the contract VmsCfg already validates — a selector and each action's parameters
 * stay JSON, with the server's message shown when it refuses one. A full visual condition editor is
 * deliberately not here (docs/future_plans/event-routing-followups.md).
 */

/** The map journal's dictionary: what VmsCfg accepts as an event name. */
const EVENT_NAMES = [
  'sensor', 'relay', 'analytics',
  'alarm_raised', 'alarm_acknowledged', 'alarm_returned', 'alarm_closed', 'alarm',
]

/** The action kinds VmsCfg validates parameters for. */
const ACTION_KINDS = ['retain_recording', 'set_relay', 'emit_event', 'raise_alarm']

type EditAction = { kind: string; params: string }

type EditForm = {
  id: string
  name: string
  enabled: boolean
  eventName: string
  sourceFilter: string
  armedCondition: string
  cooldownMs: number
  actions: EditAction[]
}

const EMPTY: EditForm = {
  id: '',
  name: '',
  enabled: true,
  // A sensor contact is what the demo's rules are written around and the most common source; the
  // dictionary is a select, so the choice is one click away.
  eventName: 'sensor',
  sourceFilter: '{}',
  armedCondition: 'any',
  cooldownMs: 0,
  actions: [{ kind: 'raise_alarm', params: '{"object_id":"source","definition":"subscription"}' }],
}

function toEdit(subscription: EventSubscriptionDto): EditForm {
  return {
    id: subscription.id ?? '',
    name: subscription.name ?? '',
    enabled: subscription.enabled ?? true,
    eventName: subscription.eventName ?? 'sensor',
    sourceFilter: subscription.sourceFilter || '{}',
    armedCondition: subscription.armedCondition || 'any',
    cooldownMs: subscription.cooldownMs ?? 0,
    actions: (subscription.actions ?? []).map((action) => ({
      kind: action.kind ?? 'raise_alarm',
      params: action.params || '{}',
    })),
  }
}

/** Validated here so a typo is a message beside the field, not a round trip; the server has the
 *  last word on what the keys and values may be. */
function parseObject(text: string, label: string): string {
  const trimmed = text.trim() || '{}'
  let parsed: unknown
  try {
    parsed = JSON.parse(trimmed)
  } catch {
    throw new Error(`${label}: ${'invalid JSON'}`)
  }
  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed))
    throw new Error(`${label}: ${'not a JSON object'}`)
  return JSON.stringify(parsed)
}

export default function EventSubscriptionsPanel() {
  const { t } = useTranslation()
  const [subscriptions, setSubscriptions] = useState<EventSubscriptionDto[]>([])
  const [loaded, setLoaded] = useState(false)
  const [form, setForm] = useState<EditForm | null>(null)
  const [snap, setSnap] = useState<EditForm | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const load = async () => {
    try {
      const response = await requestJson<{ subscriptions?: EventSubscriptionDto[] }>(
        '/api/map/admin/event-subscriptions')
      setSubscriptions(response.subscriptions ?? [])
      setError(null)
    } catch (e) {
      setError(errorText(e))
    } finally {
      setLoaded(true)
    }
  }

  useEffect(() => { void load() }, [])

  const isNew = !!form && !subscriptions.some((s) => s.id === form.id)
  const dirty = useMemo(
    () => (form && snap ? JSON.stringify(form) !== JSON.stringify(snap) : false),
    [form, snap],
  )

  const select = (subscription: EventSubscriptionDto) => {
    const next = toEdit(subscription)
    setForm(next)
    setSnap(next)
    setNotice(null)
    setError(null)
  }

  const addNew = () => {
    const next = { ...EMPTY, id: crypto.randomUUID(), actions: [...EMPTY.actions] }
    setForm(next)
    setSnap(next)
    setNotice(null)
    setError(null)
  }

  const save = async () => {
    if (!form) return
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      if (!form.name.trim())
        throw new Error(t('eventRouting.nameRequired'))

      const subscription: EventSubscriptionDto = {
        id: form.id,
        name: form.name.trim(),
        enabled: form.enabled,
        eventName: form.eventName,
        sourceFilter: parseObject(form.sourceFilter, t('eventRouting.sourceFilter')),
        armedCondition: form.armedCondition,
        cooldownMs: Math.max(0, Number(form.cooldownMs) || 0),
        actions: form.actions.map((action, position): SubscriptionActionDto => ({
          position,
          kind: action.kind,
          params: parseObject(action.params, `${action.kind} ${t('eventRouting.params')}`),
        })),
      }

      await requestJson('/api/map/admin/event-subscriptions', jsonRequest('PUT', { subscription }))
      await load()
      // The service assigns the version; re-reading brings it back with the saved row.
      const saved = (await requestJson<{ subscriptions?: EventSubscriptionDto[] }>(
        '/api/map/admin/event-subscriptions')).subscriptions?.find((s) => s.id === form.id)
      if (saved) select(saved)
      else setSnap(form)
      setNotice(t('eventRouting.saved'))
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  const remove = async () => {
    if (!form?.id) return
    setBusy(true)
    setError(null)
    try {
      await requestJson(`/api/map/admin/event-subscriptions/${enc(form.id)}`, { method: 'DELETE' })
      setForm(null)
      setSnap(null)
      await load()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  /** Enable or disable a saved row without opening it: the switch acts on the list. */
  const toggle = async (subscription: EventSubscriptionDto) => {
    setBusy(true)
    setError(null)
    try {
      // The version is the service's; sending the saved one back would be noise.
      const rest: EventSubscriptionDto = { ...subscription }
      delete rest.version
      await requestJson('/api/map/admin/event-subscriptions', jsonRequest('PUT', {
        subscription: { ...rest, enabled: !subscription.enabled },
      }))
      await load()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  const setAction = (index: number, patch: Partial<EditAction>) => {
    if (!form) return
    const actions = form.actions.map((action, at) => (at === index ? { ...action, ...patch } : action))
    setForm({ ...form, actions })
  }

  return (
    <MasterDetailLayout
      listTitle={t('eventRouting.title')}
      listWidth={260}
      listActions={(
        <Tooltip title={t('common.refresh')}>
          <IconButton size="small" onClick={() => void load()}>
            <RefreshIcon fontSize="small" />
          </IconButton>
        </Tooltip>
      )}
      onAdd={addNew}
      addTooltip={t('eventRouting.add')}
      list={(
        <List dense disablePadding>
          {subscriptions.map((subscription) => (
            <ListItemButton
              key={subscription.id}
              selected={form?.id === subscription.id}
              onClick={() => select(subscription)}
              sx={{ pr: 0.5 }}
            >
              <ListItemText
                primary={subscription.name || subscription.id}
                secondary={[
                  subscription.eventName,
                  `${subscription.actions?.length ?? 0}`,
                ].filter(Boolean).join(' · ')}
                primaryTypographyProps={{ noWrap: true }}
                secondaryTypographyProps={{ noWrap: true, variant: 'caption' }}
              />
              <Tooltip title={t('eventRouting.enabled')}>
                <Switch
                  size="small"
                  checked={!!subscription.enabled}
                  disabled={busy}
                  onClick={(event) => event.stopPropagation()}
                  onChange={() => void toggle(subscription)}
                />
              </Tooltip>
            </ListItemButton>
          ))}
          {loaded && subscriptions.length === 0 && (
            <Typography variant="body2" color="text.secondary" sx={{ p: 1.5 }}>
              {t('eventRouting.empty')}
            </Typography>
          )}
        </List>
      )}
    >
      {!form ? (
        <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>
          {t('eventRouting.pick')}
        </Typography>
      ) : (
        <Box component="form" sx={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}
          onSubmit={(event) => { event.preventDefault(); void save() }}>
          <DetailToolbar
            title={form.name || t('eventRouting.newTitle')}
            subtitle={form.id}
            dirty={dirty || isNew}
            busy={busy}
            applyLabel={t('common.apply')}
            onApply={() => void save()}
            onDelete={form ? () => void remove() : undefined}
            deleteLabel={t('common.delete')}
          />
          <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', p: 2 }}>
            <Stack spacing={2}>
              {error && <Alert severity="error" onClose={() => setError(null)}>{error}</Alert>}
              {notice && <Alert severity="success" onClose={() => setNotice(null)}>{notice}</Alert>}

              <Stack direction="row" spacing={2} flexWrap="wrap" useFlexGap>
                <TextField
                  required size="small" label={t('eventRouting.name')}
                  value={form.name}
                  onChange={(e) => setForm({ ...form, name: e.target.value })}
                  sx={{ minWidth: 220 }}
                />
                <TextField
                  select size="small" label={t('eventRouting.eventName')}
                  value={form.eventName}
                  onChange={(e) => setForm({ ...form, eventName: e.target.value })}
                  sx={{ minWidth: 200 }}
                >
                  {EVENT_NAMES.map((name) => <MenuItem key={name} value={name}>{name}</MenuItem>)}
                </TextField>
                <TextField
                  select size="small" label={t('eventRouting.armedCondition')}
                  value={form.armedCondition}
                  onChange={(e) => setForm({ ...form, armedCondition: e.target.value })}
                  sx={{ minWidth: 160 }}
                >
                  <MenuItem value="any">{t('eventRouting.armedAny')}</MenuItem>
                  <MenuItem value="armed">{t('eventRouting.armedArmed')}</MenuItem>
                  <MenuItem value="disarmed">{t('eventRouting.armedDisarmed')}</MenuItem>
                </TextField>
                <TextField
                  type="number" size="small" label={t('eventRouting.cooldownMs')}
                  value={form.cooldownMs}
                  onChange={(e) => setForm({ ...form, cooldownMs: Number(e.target.value) })}
                  slotProps={{ htmlInput: { min: 0 } }}
                  sx={{ width: 160 }}
                />
                <FormControlLabel
                  control={(
                    <Switch
                      checked={form.enabled}
                      onChange={(e) => setForm({ ...form, enabled: e.target.checked })}
                    />
                  )}
                  label={t('eventRouting.enabled')}
                />
              </Stack>

              <Box>
                <Typography variant="subtitle2">{t('eventRouting.sourceFilter')}</Typography>
                <Typography variant="caption" color="text.secondary" display="block" sx={{ mb: 0.5 }}>
                  {t('eventRouting.sourceFilterHint')}
                </Typography>
                <TextField
                  fullWidth multiline minRows={3} maxRows={10}
                  value={form.sourceFilter}
                  onChange={(e) => setForm({ ...form, sourceFilter: e.target.value })}
                  slotProps={{ input: { sx: { fontFamily: 'monospace', fontSize: 13 } } }}
                />
              </Box>

              <Divider />

              <Stack direction="row" alignItems="center" spacing={1}>
                <Typography variant="subtitle2" sx={{ flex: 1 }}>{t('eventRouting.actions')}</Typography>
                <Button
                  size="small" startIcon={<AddIcon fontSize="small" />}
                  onClick={() => setForm({
                    ...form,
                    actions: [...form.actions, { kind: 'raise_alarm', params: '{"object_id":"source","definition":"subscription"}' }],
                  })}
                >
                  {t('eventRouting.addAction')}
                </Button>
              </Stack>

              {form.actions.map((action, index) => (
                <Box key={index} sx={{ border: 1, borderColor: 'divider', borderRadius: 1, p: 1.5 }}>
                  <Stack direction="row" alignItems="center" spacing={1} sx={{ mb: 1 }}>
                    <Typography variant="caption" color="text.secondary" sx={{ flex: 1 }}>
                      {t('eventRouting.position', { position: index })}
                    </Typography>
                    <Tooltip title={t('common.delete')}>
                      <span>
                        <IconButton
                          size="small" disabled={form.actions.length === 1}
                          onClick={() => setForm({
                            ...form,
                            actions: form.actions.filter((_, at) => at !== index),
                          })}
                        >
                          <DeleteIcon fontSize="small" />
                        </IconButton>
                      </span>
                    </Tooltip>
                  </Stack>
                  <Stack direction={{ xs: 'column', md: 'row' }} spacing={1.5}>
                    <TextField
                      select size="small" label={t('eventRouting.kind')}
                      value={action.kind}
                      onChange={(e) => setAction(index, { kind: e.target.value })}
                      sx={{ minWidth: 200 }}
                    >
                      {ACTION_KINDS.map((kind) => <MenuItem key={kind} value={kind}>{kind}</MenuItem>)}
                    </TextField>
                    <TextField
                      fullWidth multiline minRows={2} maxRows={8}
                      label={t('eventRouting.params')}
                      value={action.params}
                      onChange={(e) => setAction(index, { params: e.target.value })}
                      slotProps={{ input: { sx: { fontFamily: 'monospace', fontSize: 13 } } }}
                    />
                  </Stack>
                </Box>
              ))}
            </Stack>
          </Box>
        </Box>
      )}
    </MasterDetailLayout>
  )
}
