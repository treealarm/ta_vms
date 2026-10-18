import { useState } from 'react'
import { Box, List, ListItemButton, ListItemText, TextField, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'

export type MapObjectItem = {
  key: string
  primary: string
  secondary?: React.ReactNode
  /** Status colour, drawn as a dot before the name. */
  dot?: string
  selected?: boolean
  onClick: () => void
}

export type MapObjectSection = {
  title: string
  /** What the section is for, under its title. */
  hint?: string
  /** Shown instead of the items when there are none at all (not when the filter hides them). */
  empty?: string
  items: MapObjectItem[]
}

/**
 * What is on a map, as a list beside it: finding a camera by name beats panning around for it.
 * One filter over every section, since the reader is looking for a name, not a kind.
 */
export default function MapObjectList({ sections }: { sections: MapObjectSection[] }) {
  const { t } = useTranslation()
  const [filter, setFilter] = useState('')
  const needle = filter.trim().toLocaleLowerCase()
  const total = sections.reduce((n, s) => n + s.items.length, 0)

  return (
    <Box>
      {total > 5 && (
        <TextField
          size="small"
          fullWidth
          placeholder={t('maps.findOnMap')}
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          sx={{ px: 1.5, pt: 1.5 }}
        />
      )}
      {sections.map((section) => {
        const shown = needle ? section.items.filter((i) => i.primary.toLocaleLowerCase().includes(needle)) : section.items
        if (needle && shown.length === 0) return null
        return (
          <Box key={section.title}>
            <Typography variant="subtitle2" sx={{ px: 1.5, pt: 1.5 }}>{section.title}</Typography>
            {section.hint && (
              <Typography variant="caption" color="text.secondary" sx={{ px: 1.5, display: 'block' }}>{section.hint}</Typography>
            )}
            {section.items.length === 0 && section.empty ? (
              <Typography variant="body2" color="text.disabled" sx={{ px: 1.5, py: 1 }}>{section.empty}</Typography>
            ) : (
              <List dense>
                {shown.map((item) => (
                  <ListItemButton key={item.key} selected={item.selected} onClick={item.onClick}>
                    {item.dot && (
                      <Box sx={{ width: 10, height: 10, borderRadius: '50%', bgcolor: item.dot, mr: 1, flexShrink: 0 }} />
                    )}
                    <ListItemText
                      primary={item.primary}
                      primaryTypographyProps={{ noWrap: true }}
                      secondary={item.secondary}
                      secondaryTypographyProps={{ component: 'div' }}
                    />
                  </ListItemButton>
                ))}
              </List>
            )}
          </Box>
        )
      })}
    </Box>
  )
}
