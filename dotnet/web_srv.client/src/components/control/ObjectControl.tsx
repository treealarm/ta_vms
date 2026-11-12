import { useEffect, useState } from 'react'
import { Alert, Box, Chip, Stack, Switch, Tooltip, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'

import { setCameraArmed } from '../../api/controlApi'
import { setDigitalInputArmMode } from '../../api/deviceIoApi'
import { errorText } from '../../api/requestJson'
import RelaySwitch from './RelaySwitch'
import type { ControlObjectDto } from '../../dto/dtos'

type Props = {
  row: ControlObjectDto
  /** Re-read the list after an action lands, so the row and its neighbours tell the truth. */
  onChanged: () => void
  /** Show what the object is: the panel's detail pane does, the map's inspector already has. */
  heading?: boolean
}

/**
 * What an operator can do with one object, and what it currently says. One component for the
 * Control panel and the map's inspector, so both act through the same three commands:
 *
 *  - a camera's own armed flag (`PUT /api/cameras/{id}/state`),
 *  - a sensor's mode (`SetDeviceArmMode`) — only when an administrator configured the input,
 *  - a relay's state (VmsOnvif), which is never "arming": a relay is switched, not guarded.
 *
 * A click answers at once and is rolled back when the server refuses it; the next read replaces
 * the guess.
 */
export default function ObjectControl({ row, onChanged, heading = false }: Props) {
  const { t } = useTranslation()
  const [optimistic, setOptimistic] = useState<boolean | null>(null)
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // What the effective arming resolves to; a relay's state has a switch of its own (RelaySwitch).
  const observed = row.armed
  const value = optimistic ?? observed

  useEffect(() => {
    if (optimistic !== null && observed === optimistic) setOptimistic(null)
  }, [observed, optimistic])

  // A different object must not inherit the previous one's guess or error.
  useEffect(() => {
    setOptimistic(null)
    setError(null)
  }, [row.objectId])

  const toggle = async () => {
    const next = !value
    setPending(true)
    setError(null)
    setOptimistic(next)
    try {
      if (row.kind === 'camera')
        await setCameraArmed(row.sourceId, next)
      else
        await setDigitalInputArmMode(row.sourceId, row.partToken, next ? 'armed' : 'disarmed')
      onChanged()
    } catch (e) {
      setError(errorText(e))
      setOptimistic(null)
      onChanged()
    } finally {
      setPending(false)
    }
  }

  const armedLabel =
    row.armingSource === 'space'
      ? (value ? t('onvifSettings.deviceIoGuardOnZone') : t('onvifSettings.deviceIoGuardOffZone'))
      : row.armingSource === 'camera'
        ? (value ? t('onvifSettings.deviceIoGuardOnCamera') : t('onvifSettings.deviceIoGuardOffCamera'))
        : (value ? t('onvifSettings.deviceIoGuardOn') : t('onvifSettings.deviceIoGuardOff'))

  // Nothing to write the mode to, or no right to write it: the switch is shown, not usable, with
  // the reason in the tooltip instead of a click that fails.
  const blockedReason =
    row.kind === 'digital_input' && !row.configured
      ? t('control.notConfigured')
      : !row.canControl
        ? t('onvifSettings.deviceIoNoControl')
        : null

  const controlSwitch = (
    <Switch
      size="small"
      checked={value}
      disabled={pending || blockedReason !== null}
      onChange={() => void toggle()}
    />
  )

  // Only a contact has a state apart from its action: a relay's is the switch's own label.
  const stateLabel = row.kind === 'digital_input'
    ? row.state === 'open'
      ? t('control.stateOpen')
      : row.state === 'closed'
        ? t('control.stateClosed')
        : t('control.stateUnknown')
    : null

  return (
    <Stack spacing={1.25}>
      {heading && (
        <Box>
          <Typography variant="subtitle1" fontWeight={600}>{row.name}</Typography>
          <Typography variant="caption" color="text.secondary">
            {t(`control.kindLabel.${row.kind}`)}
            {row.cameraName && row.kind !== 'camera' ? ` · ${row.cameraName}` : ''}
            {row.spaceName ? ` · ${row.spaceName}` : ''}
          </Typography>
        </Box>
      )}

      {stateLabel !== null && (
        <Stack direction="row" spacing={1} alignItems="center">
          <Typography variant="body2" color="text.secondary" sx={{ minWidth: 110 }}>
            {t('control.state')}
          </Typography>
          <Chip
            size="small"
            variant="outlined"
            color={row.state === 'active' || row.state === 'closed' ? 'warning' : 'default'}
            label={stateLabel}
          />
        </Stack>
      )}

      {row.hasArming && (
        <Stack direction="row" spacing={1} alignItems="center">
          <Typography variant="body2" color="text.secondary" sx={{ minWidth: 110 }}>
            {t('control.guard')}
          </Typography>
          {blockedReason ? (
            <Tooltip title={blockedReason}><span>{controlSwitch}</span></Tooltip>
          ) : controlSwitch}
          <Typography
            variant="caption"
            color={value ? 'success.main' : 'text.secondary'}
            title={t('onvifSettings.deviceIoGuardHint')}
          >
            {armedLabel}
          </Typography>
        </Stack>
      )}

      {row.kind === 'relay' && (
        <Stack direction="row" spacing={1} alignItems="center">
          <Typography variant="body2" color="text.secondary" sx={{ minWidth: 110 }}>
            {t('control.relay')}
          </Typography>
          <RelaySwitch row={row} onChanged={onChanged} />
        </Stack>
      )}

      {row.kind === 'digital_input' && row.alarmEnabled && (
        <Typography variant="caption" color="text.secondary">{t('control.alarmEnabled')}</Typography>
      )}

      {error && <Alert severity="error" sx={{ fontSize: '0.8rem' }}>{error}</Alert>}
    </Stack>
  )
}
