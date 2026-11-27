import { Box, Tab, Tabs, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'

import type { NavGroup } from './navGroups'

type Props = {
  groups: readonly NavGroup[]
  /** Route segment of the open tab. A slug no group holds leaves every group unselected. */
  current: string
  onSelect: (slug: string) => void
}

/**
 * The vertical navigation panel shared by both views: one titled group per topic, its tabs
 * underneath. Slugs are route segments, so regrouping never touches a deep link.
 */
export default function GroupedNav({ groups, current, onSelect }: Props) {
  const { t } = useTranslation()

  return (
    <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', px: 0.5, pt: 1 }}>
      {groups.map((group) => {
        const selected = group.items.some((item) => item.slug === current)
        return (
          <Box key={group.key} sx={{ mb: 1.5, '&:last-of-type': { mb: 0 } }}>
            <Typography
              variant="caption"
              sx={{
                display: 'block',
                px: 1.5,
                pb: 0.25,
                color: 'text.secondary',
                fontWeight: 700,
                fontSize: '0.62rem',
                letterSpacing: 0.6,
                textTransform: 'uppercase',
              }}
            >
              {t(group.titleKey)}
            </Typography>
            <Tabs
              orientation="vertical"
              value={selected ? current : false}
              onChange={(_event, slug: string) => onSelect(slug)}
              sx={{
                '& .MuiTab-root': {
                  minWidth: 0,
                  minHeight: 30,
                  py: 0.4,
                  px: 1.5,
                  alignItems: 'flex-start',
                  textAlign: 'left',
                  textTransform: 'none',
                  whiteSpace: 'nowrap',
                  fontSize: '0.8rem',
                },
              }}
            >
              {group.items.map((item) => (
                <Tab key={item.slug} value={item.slug} label={t(item.labelKey)} sx={{ width: '100%' }} />
              ))}
            </Tabs>
          </Box>
        )
      })}
    </Box>
  )
}
