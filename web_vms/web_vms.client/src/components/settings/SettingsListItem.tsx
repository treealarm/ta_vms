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
}

/** Standard left-list row for admin settings pages. */
export default function SettingsListItem({
  selected, onClick, primary, secondary, depth = 0, dimmed,
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
        primaryTypographyProps={{ noWrap: true, component: 'div' }}
        secondaryTypographyProps={{ noWrap: true, component: 'div' }}
      />
    </ListItemButton>
  )
}
