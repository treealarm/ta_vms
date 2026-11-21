import { useState } from 'react'
import {
  Button,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'

import { localDateTimeToIso } from './bypassTime'

type Props = {
  open: boolean
  busy?: boolean
  onClose: () => void
  /** `null` is an indefinite exclusion; an ISO string is an absolute deadline. */
  onApply: (until: string | null) => void
}

const PRESETS = [
  { minutes: 30, key: 'control.bypassPreset30' },
  { minutes: 60, key: 'control.bypassPreset60' },
  { minutes: 120, key: 'control.bypassPreset120' },
  { minutes: 240, key: 'control.bypassPreset240' },
]

/**
 * The deadline picker shared by the Control panel, the map's inspector and the camera card.
 * The operator may exclude the node indefinitely or until a moment; the moment is sent as an
 * absolute instant, so the server never parses a wall-clock string.
 */
export default function BypassUntilDialog({ open, busy = false, onClose, onApply }: Props) {
  const { t } = useTranslation()
  const [custom, setCustom] = useState('')

  // The dialog stays mounted between openings, so its own field is cleared by the action that
  // closes it rather than by an effect that would re-render on every open.
  const close = () => {
    setCustom('')
    onClose()
  }
  const apply = (until: string | null) => {
    setCustom('')
    onApply(until)
  }

  return (
    <Dialog open={open} onClose={busy ? undefined : close} maxWidth="xs" fullWidth>
      <DialogTitle>{t('control.bypassUntil')}</DialogTitle>
      <DialogContent>
        <Stack spacing={1.5}>
          <Typography variant="body2" color="text.secondary">
            {t('control.bypassUntilHint')}
          </Typography>
          <Stack direction="row" spacing={1} useFlexGap flexWrap="wrap">
            {PRESETS.map((preset) => (
              <Button
                key={preset.minutes}
                size="small"
                variant="outlined"
                disabled={busy}
                onClick={() =>
                  apply(new Date(Date.now() + preset.minutes * 60_000).toISOString())}
              >
                {t(preset.key)}
              </Button>
            ))}
          </Stack>
          <TextField
            size="small"
            type="datetime-local"
            label={t('control.bypassAt')}
            value={custom}
            onChange={(event) => setCustom(event.target.value)}
            InputLabelProps={{ shrink: true }}
          />
          <Button
            variant="contained"
            disabled={busy || custom === ''}
            onClick={() => {
              const until = localDateTimeToIso(custom)
              if (until) apply(until)
            }}
          >
            {t('common.apply')}
          </Button>
          <Button color="inherit" disabled={busy} onClick={() => apply(null)}>
            {t('control.bypassForever')}
          </Button>
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={close} disabled={busy}>{t('common.cancel')}</Button>
      </DialogActions>
    </Dialog>
  )
}
