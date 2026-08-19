import type { ReactNode } from 'react'
import { Box, Stack, Tooltip } from '@mui/material'
import InfoOutlinedIcon from '@mui/icons-material/InfoOutlined'

type Props = {
  hint: string
  children: ReactNode
}

/**
 * A form control with its explanation moved into a tooltip.
 *
 * Helper text under every field turns a settings form into a wall of prose that
 * nobody reads twice, and the fields it separates stop looking like one form. The
 * icon keeps the explanation one hover away for whoever needs it the first time.
 */
export default function FieldHint({ hint, children }: Props) {
  return (
    <Stack direction="row" spacing={0.5} alignItems="center">
      <Box sx={{ flex: 1, minWidth: 0 }}>{children}</Box>
      <Tooltip title={hint} placement="left">
        <InfoOutlinedIcon fontSize="small" color="disabled" sx={{ cursor: 'help', flexShrink: 0 }} />
      </Tooltip>
    </Stack>
  )
}
