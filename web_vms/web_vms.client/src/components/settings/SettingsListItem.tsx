import { ListItemButton, ListItemText } from '@mui/material'

type Props = {
  selected: boolean
  onClick: () => void
  primary: React.ReactNode
  secondary?: React.ReactNode
  /** Tree indentation level (cameras page: server 0 / camera 1 / stream 2). */
  depth?: 0 | 1 | 2
  /** Renders the row at reduced opacity (disabled cameras/streams). */
  dimmed?: boolean
  /**
   * Let content wrap to multiple lines instead of the default single-line
   * ellipsis. Required for chip rows — ellipsis does not render for flex
   * children, they would be hard-clipped by the narrow list column.
   */
  wrap?: boolean
}

/** Standard left-list row for admin settings pages. */
export default function SettingsListItem({
  selected, onClick, primary, secondary, depth = 0, dimmed, wrap = false,
}: Props) {
  return (
    <ListItemButton
      dense
      selected={selected}
      onClick={onClick}
      sx={{ pl: 1.5 + depth * 1.5, opacity: dimmed ? 0.45 : 1 }}
    >
      <ListItemText
        primary={primary}
        secondary={secondary}
        // div (not span/p) so chips and stacks are valid inside either line
        primaryTypographyProps={{ noWrap: !wrap, component: 'div' }}
        secondaryTypographyProps={{ noWrap: !wrap, component: 'div' }}
      />
    </ListItemButton>
  )
}
