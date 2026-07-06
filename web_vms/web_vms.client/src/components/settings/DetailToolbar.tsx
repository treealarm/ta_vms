import { Box, Button, Stack, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'

type Props = {
  title: React.ReactNode
  subtitle?: React.ReactNode
  /** Apply/Cancel are disabled when false; create forms pass their validity here. */
  dirty: boolean
  busy?: boolean
  applyLabel?: string
  /** Omit to render Apply as type="submit" — the toolbar must then live inside the panel's <form>. */
  onApply?: () => void
  hideApply?: boolean
  onCancel?: () => void
  onDelete?: () => void
  deleteLabel?: string
  /** Right-aligned slot for immediate actions (arm switch, subscription toggle, chips). */
  extra?: React.ReactNode
}

/**
 * Pinned action row at the top of a settings detail pane — the buttons stay
 * visible regardless of how tall the form below is.
 */
export default function DetailToolbar({
  title, subtitle, dirty, busy, applyLabel, onApply, hideApply, onCancel, onDelete, deleteLabel, extra,
}: Props) {
  const { t } = useTranslation()
  return (
    <Stack direction="row" spacing={1} alignItems="center" sx={{ px: 2, py: 1, borderBottom: 1, borderColor: 'divider', flexShrink: 0, minHeight: 52 }}>
      <Box sx={{ minWidth: 0, mr: 1 }}>
        <Typography variant="subtitle1" fontWeight={600} noWrap>{title}</Typography>
        {subtitle && (
          <Typography variant="caption" color="text.disabled" noWrap display="block">{subtitle}</Typography>
        )}
      </Box>
      {!hideApply && (
        <Button type={onApply ? 'button' : 'submit'} variant="outlined" size="small" disabled={!dirty || busy} onClick={onApply}>
          {applyLabel ?? t('common.apply')}
        </Button>
      )}
      {onCancel && (
        <Button variant="text" size="small" disabled={!dirty || busy} onClick={onCancel}>
          {t('common.cancel')}
        </Button>
      )}
      {onDelete && (
        <Button color="error" variant="outlined" size="small" disabled={busy} onClick={onDelete}>
          {deleteLabel ?? t('common.delete')}
        </Button>
      )}
      {extra && <Box sx={{ ml: 'auto', flexShrink: 0 }}>{extra}</Box>}
    </Stack>
  )
}
