import { useEffect, useState } from 'react'
import { Stack, Switch, Tooltip, Typography } from '@mui/material'
import ErrorOutlineIcon from '@mui/icons-material/ErrorOutline'
import { useTranslation } from 'react-i18next'

import type { ControlObjectDto } from '../../dto/dtos'
import { errorText } from '../../api/requestJson'
import { setRelayOutputState } from '../../hooks/useRelayToggle'

type Props = {
  row: ControlObjectDto
  /** Re-read the list after the command lands, so the row and its neighbours tell the truth. */
  onChanged: () => void
}

/**
 * A relay's state, and the switch that sets it. One component for the panel's list, its detail
 * pane and the map's inspector: a relay is driven, not watched, so the only action it has is this
 * one — and the label beside the switch follows the click at once, because the camera reports the
 * new state a moment later. Without that the switch would look like it had not worked.
 */
export default function RelaySwitch({ row, onChanged }: Props) {
  const { t } = useTranslation()
  const [optimistic, setOptimistic] = useState<boolean | null>(null)
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const observed = row.state === 'active'
  const value = optimistic ?? observed

  useEffect(() => {
    if (optimistic !== null && observed === optimistic) setOptimistic(null)
  }, [observed, optimistic])

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
      await setRelayOutputState(row.sourceId, row.partToken, next)
      onChanged()
    } catch (e) {
      setError(errorText(e))
      setOptimistic(null)
      onChanged()
    } finally {
      setPending(false)
    }
  }

  return (
    <Stack
      direction="row"
      spacing={0.5}
      alignItems="center"
      // A row is a button of its own: acting on the switch must not also select the row under it.
      onClick={(event) => event.stopPropagation()}
    >
      <Switch
        size="small"
        checked={value}
        disabled={pending || !row.canControl}
        onChange={() => void toggle()}
      />
      <Typography variant="caption" color={value ? 'warning.main' : 'text.secondary'}>
        {value ? t('control.stateActive') : t('control.stateInactive')}
      </Typography>
      {error && (
        <Tooltip title={error}>
          <ErrorOutlineIcon fontSize="small" color="error" />
        </Tooltip>
      )}
    </Stack>
  )
}
