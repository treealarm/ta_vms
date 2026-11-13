import { useEffect, useState } from 'react'
import {
  Alert, Button, CircularProgress, FormControlLabel, MenuItem, Stack, Switch, TextField,
  Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'

import {
  getDeviceSettings, getNodeSettings, setDeviceSettings, setNodeSettings,
} from '../../api/controlApi'
import { errorText } from '../../api/requestJson'
import type { CameraArmMode, ObjectSettingsBody, ObjectSettingsDto } from '../../dto/dtos'
import { guardInheritedColor } from '../../theme/ntTheme'

type Props =
  /** A node selected in the tree or on the map; a replica resolves to the device it shows. */
  | { nodeId: string }
  /** A card that holds the camera and the part token, not the node id. */
  | { cameraId: string; partKind: 'device' | 'digital_input'; partToken?: string }

/**
 * The settings of a logical device object: who decides its arming (`Inherit` follows the zone it
 * stands in) and, for a digital input, which contact state raises an alarm. The operator's switch
 * stays a two-state command; this is the administrator's card.
 */
export default function ObjectSettingsCard(props: Props) {
  const { t } = useTranslation()
  const [settings, setSettings] = useState<ObjectSettingsDto | null>(null)
  const [mode, setMode] = useState<CameraArmMode>('inherit')
  const [alarmEnabled, setAlarmEnabled] = useState(false)
  const [triggerOn, setTriggerOn] = useState<'open' | 'closed'>('closed')
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = async () => {
    setLoading(true)
    setError(null)
    try {
      const next = 'nodeId' in props
        ? await getNodeSettings(props.nodeId)
        : await getDeviceSettings(props.cameraId, props.partKind, props.partToken ?? '')
      apply(next)
    } catch (e) {
      setError(errorText(e))
      setSettings(null)
    } finally {
      setLoading(false)
    }
  }

  const apply = (next: ObjectSettingsDto) => {
    setSettings(next)
    setMode(next.armMode ?? 'inherit')
    setAlarmEnabled(next.alarmEnabled)
    setTriggerOn(next.triggerOn ?? 'closed')
  }

  // The card is per object: a selection change must not keep the previous object's values.
  const key = 'nodeId' in props ? props.nodeId : `${props.cameraId}/${props.partKind}/${props.partToken ?? ''}`
  useEffect(() => {
    void load()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key])

  const save = async () => {
    setBusy(true)
    setError(null)
    try {
      const body: ObjectSettingsBody = {
        armMode: mode,
        triggerOn: settings?.partKind === 'digital_input' ? triggerOn : '',
        alarmEnabled: settings?.partKind === 'digital_input' ? alarmEnabled : false,
      }
      const next = 'nodeId' in props
        ? await setNodeSettings(props.nodeId, body)
        : await setDeviceSettings(props.cameraId, props.partKind, props.partToken ?? '', body)
      apply(next)
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  if (loading && settings === null)
    return <CircularProgress size={20} />

  if (settings === null)
    return error ? <Alert severity="error">{error}</Alert> : null

  const isInput = settings.partKind === 'digital_input'
  const viaZone = settings.armed && settings.armingSource === 'space'
  const stateLabel = settings.armed ? t('objectSettings.armed') : t('objectSettings.disarmed')

  return (
    <Stack spacing={1.5}>
      <TextField
        select
        size="small"
        label={t('objectSettings.arming')}
        value={mode}
        disabled={busy || !settings.configured && isInput}
        onChange={(e) => setMode(e.target.value as CameraArmMode)}
        helperText={t('objectSettings.inheritHint')}
        sx={{ maxWidth: 280 }}
      >
        <MenuItem value="inherit">{t('objectSettings.inherit')}</MenuItem>
        <MenuItem value="armed">{t('objectSettings.armed')}</MenuItem>
        <MenuItem value="disarmed">{t('objectSettings.disarmed')}</MenuItem>
      </TextField>

      {isInput && (
        <>
          <FormControlLabel
            control={
              <Switch
                checked={alarmEnabled}
                disabled={busy || !settings.configured}
                onChange={(e) => setAlarmEnabled(e.target.checked)}
              />
            }
            label={t('objectSettings.alarmOnContact')}
          />
          <TextField
            select
            size="small"
            label={t('objectSettings.triggerOn')}
            value={triggerOn}
            disabled={busy || !alarmEnabled || !settings.configured}
            onChange={(e) => setTriggerOn(e.target.value as 'open' | 'closed')}
            sx={{ maxWidth: 220 }}
          >
            <MenuItem value="open">{t('objectSettings.triggerOpen')}</MenuItem>
            <MenuItem value="closed">{t('objectSettings.triggerClosed')}</MenuItem>
          </TextField>
        </>
      )}

      {!settings.configured && isInput && (
        <Typography variant="caption" color="text.secondary">
          {t('objectSettings.notConfigured')}
        </Typography>
      )}

      <Typography
        variant="caption"
        // Green when the object's own mode settled it, purple when its zone did, grey when it is
        // not on guard — the same reading the guard of a container gives.
        color={!settings.armed ? 'text.secondary' : viaZone ? guardInheritedColor : 'success.main'}
      >
        {t('objectSettings.effective', {
          state: stateLabel,
          suffix: viaZone ? ` (${t('objectSettings.viaZone')})` : '',
        })}
      </Typography>

      <Stack direction="row" spacing={1} alignItems="center">
        <Button variant="contained" size="small" disabled={busy} onClick={() => void save()}>
          {t('objectSettings.save')}
        </Button>
        {error && <Alert severity="error" sx={{ py: 0 }}>{error}</Alert>}
      </Stack>
    </Stack>
  )
}
