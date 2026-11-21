import { useEffect, useState } from 'react'
import { Alert, Box, Button, Chip, Stack, Switch, Tooltip, Typography } from '@mui/material'
import TimerOutlinedIcon from '@mui/icons-material/TimerOutlined'
import { useTranslation } from 'react-i18next'

import { setNodeArming } from '../../api/controlApi'
import { errorText } from '../../api/requestJson'
import BypassUntilDialog from './BypassUntilDialog'
import { formatUntil } from './bypassTime'
import { guardState } from './guardState'
import RelaySwitch from './RelaySwitch'
import type { ControlObjectDto, NodeArmingDto } from '../../dto/dtos'

type Props = {
  row: ControlObjectDto
  /** Re-read the list after an action lands, so the row and its neighbours tell the truth. */
  onChanged: () => void
  /** Show what the object is: the panel's detail pane does, the map's inspector already has. */
  heading?: boolean
}

/**
 * What an operator can do with one object, and what it currently says. One component for the
 * Control panel and the map's inspector, so both act through the same commands:
 *
 *  - the guard switch of a camera or a sensor (`SetNodeArming`, the one facade — a camera
 *    delegates to VmsCfg's flag and an input to its arm_mode),
 *  - a relay's state (VmsOnvif), which is never "arming": a relay is switched, not guarded.
 *
 * A click is not painted optimistically: the effective arming is a property of the whole ancestor
 * chain. A device left in `inherit` follows its zone; an explicit on or off is the operator's own
 * decision and outranks it, so a sensor switched off inside an armed zone really is off guard, and
 * only the server's answer says what happened. The row is re-read after the answer lands.
 */
export default function ObjectControl({ row, onChanged, heading = false }: Props) {
  const { t, i18n } = useTranslation()
  const [applied, setApplied] = useState<NodeArmingDto | null>(null)
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [bypassOpen, setBypassOpen] = useState(false)

  // What the effective arming resolves to; a relay's state has a switch of its own (RelaySwitch).
  const value = applied?.armed ?? row.armed
  const source = applied?.source ?? row.armingSource
  const bypassed = applied?.bypassed ?? row.bypassed
  const bypassUntil = applied?.bypassUntil ?? row.bypassUntil
  const state = guardState({
    armed: value,
    source,
    bypassed,
    bypassUntil,
    surface: 'device',
  })

  // A different object must not inherit the previous one's answer or error.
  useEffect(() => {
    setApplied(null)
    setError(null)
    setBypassOpen(false)
  }, [row.objectId])

  const run = async (action: () => Promise<NodeArmingDto>) => {
    setPending(true)
    setError(null)
    try {
      setApplied(await action())
      onChanged()
    } catch (e) {
      setError(errorText(e))
      setApplied(null)
      onChanged()
    } finally {
      setPending(false)
    }
  }

  const toggle = () =>
    void run(() => setNodeArming(row.objectId, value ? 'disarmed' : 'armed'))

  const applyBypass = (until: string | null) => {
    setBypassOpen(false)
    void run(() => setNodeArming(row.objectId, 'disarmed', until))
  }

  const label = state.timed && bypassUntil
    ? t(state.labelKey, { time: formatUntil(bypassUntil, i18n.language) })
    : t(state.labelKey)

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
        <Stack spacing={0.75}>
          <Stack direction="row" spacing={1} alignItems="center">
            <Typography variant="body2" color="text.secondary" sx={{ minWidth: 110 }}>
              {t('control.guard')}
            </Typography>
            {blockedReason ? (
              <Tooltip title={blockedReason}><span>{controlSwitch}</span></Tooltip>
            ) : controlSwitch}
            <Typography
              variant="caption"
              // Grey when it is not on guard, amber when an armed zone excludes it, purple when a
              // zone settled the guard, green when it was set here.
              color={state.color}
              title={t('onvifSettings.deviceIoGuardHint')}
            >
              {label}
            </Typography>
          </Stack>
          <Stack direction="row" spacing={1} alignItems="center" useFlexGap flexWrap="wrap">
            <Button
              size="small"
              variant="outlined"
              startIcon={<TimerOutlinedIcon />}
              disabled={pending || blockedReason !== null}
              onClick={() => setBypassOpen(true)}
            >
              {t('control.bypassUntil')}
            </Button>
            {state.canReturnToZone && (
              <Button
                size="small"
                disabled={pending}
                onClick={() => void run(() => setNodeArming(row.objectId, 'inherit'))}
              >
                {t('control.returnToZone')}
              </Button>
            )}
          </Stack>
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

      <BypassUntilDialog
        open={bypassOpen}
        busy={pending}
        onClose={() => setBypassOpen(false)}
        onApply={applyBypass}
      />
    </Stack>
  )
}
