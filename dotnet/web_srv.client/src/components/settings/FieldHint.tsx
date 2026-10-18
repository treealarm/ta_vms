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
      {/* A flex container, not a plain block, so the wrapped control is laid out the same way
          it would be as a direct child of the surrounding form Stack. MUI's form controls --
          FormControlLabel, TextField -- are inline-flex, so inside a block they sit on a line
          box and pick up its strut and vertical-align: middle, which adds a few pixels of
          height and drops them below their unwrapped neighbours. Blockifying them removes the
          line box, and a hinted switch lines up with an unhinted one again. */}
      <Box sx={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>{children}</Box>
      <Tooltip title={hint} placement="left">
        <InfoOutlinedIcon fontSize="small" color="disabled" sx={{ cursor: 'help', flexShrink: 0 }} />
      </Tooltip>
    </Stack>
  )
}
