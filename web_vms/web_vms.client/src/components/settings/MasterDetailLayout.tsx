import { Box, IconButton, Stack, Tooltip, Typography } from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import { useTranslation } from 'react-i18next'

type Props = {
  listTitle: string
  /** "+" button in the left header. Receives the click event so callers can anchor a Menu. Omit to hide. */
  onAdd?: (e: React.MouseEvent<HTMLElement>) => void
  addTooltip?: string
  addDisabled?: boolean
  listWidth?: number
  /** Scrollable left-column content: a <List> of SettingsListItem or a hand-rolled tree. */
  list: React.ReactNode
  /** Optional non-scrolling row below the split view (e.g. page-level buttons). */
  footer?: React.ReactNode
  /** Right detail area; children own the DetailToolbar + scrolling body structure. */
  children: React.ReactNode
}

/**
 * Shared admin-settings page skeleton: fixed-width left list with a pinned
 * header ("+" to add) and a right detail pane, both scrolling independently.
 * The whole layout fills its parent and never overflows the page.
 */
export default function MasterDetailLayout({
  listTitle, onAdd, addTooltip, addDisabled, listWidth = 220, list, footer, children,
}: Props) {
  const { t } = useTranslation()
  return (
    <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', gap: 1 }}>
      <Box sx={{ flex: 1, minHeight: 0, display: 'flex', border: 1, borderColor: 'divider', borderRadius: 1, overflow: 'hidden' }}>
        {/* Left: list column */}
        <Box sx={{ width: listWidth, flexShrink: 0, borderRight: 1, borderColor: 'divider', display: 'flex', flexDirection: 'column', minHeight: 0 }}>
          <Stack direction="row" alignItems="center" sx={{ pl: 1.5, pr: 0.5, py: 0.5, borderBottom: 1, borderColor: 'divider', flexShrink: 0, minHeight: 40 }}>
            <Typography variant="subtitle2" noWrap sx={{ flex: 1 }}>{listTitle}</Typography>
            {onAdd && (
              <Tooltip title={addTooltip ?? t('common.add')}>
                <span>
                  <IconButton size="small" onClick={onAdd} disabled={addDisabled}>
                    <AddIcon fontSize="small" />
                  </IconButton>
                </span>
              </Tooltip>
            )}
          </Stack>
          <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>{list}</Box>
        </Box>

        {/* Right: detail pane */}
        <Box sx={{ flex: 1, minWidth: 0, minHeight: 0, display: 'flex', flexDirection: 'column' }}>{children}</Box>
      </Box>
      {footer}
    </Box>
  )
}
