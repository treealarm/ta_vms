import { alpha, Box, Button, Stack, Typography, useTheme } from '@mui/material'
import RefreshIcon from '@mui/icons-material/Refresh'
import { useTranslation } from 'react-i18next'

import { noAnswerSx, segmentSx, unavailableSx } from './timelineStyles'

type Props = {
  /** The coverage request failed, so the lanes are hatched rather than empty. */
  failed: boolean
  onRetry: () => void
}

/**
 * What the timeline means, drawn with the same helpers as the lanes themselves. "Archive
 * unavailable" and "the camera did not answer" are separate entries on purpose: from an empty bar
 * they are indistinguishable, and only the second is the camera's fault.
 */
export default function WallTimelineLegend({ failed, onRetry }: Props) {
  const { t } = useTranslation()
  const theme = useTheme()

  const samples = [
    { key: 'recorded', labelKey: 'wall.legendRecorded', sx: segmentSx() },
    {
      key: 'noRecording',
      labelKey: 'wall.legendNoRecording',
      sx: { bgcolor: alpha(theme.palette.text.primary, 0.05) },
    },
    { key: 'unavailable', labelKey: 'wall.legendUnavailable', sx: unavailableSx(theme) },
    { key: 'noAnswer', labelKey: 'wall.legendNoAnswer', sx: noAnswerSx(theme) },
    { key: 'recovered', labelKey: 'wall.legendRecovered', sx: segmentSx({ recovered: true }) },
    { key: 'recompressed', labelKey: 'wall.legendRecompressed', sx: segmentSx({ recompressed: true }) },
  ]

  return (
    <Box sx={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 1.5, mt: 0.75, px: 0.5 }}>
      {samples.map((sample) => (
        <Stack key={sample.key} direction="row" alignItems="center" spacing={0.5}>
          <Box
            sx={{
              width: 18,
              height: 10,
              borderRadius: '2px',
              flexShrink: 0,
              border: '1px solid',
              borderColor: 'divider',
              ...sample.sx,
            }}
          />
          <Typography sx={{ fontSize: '0.6rem', color: 'text.secondary', whiteSpace: 'nowrap' }}>
            {t(sample.labelKey)}
          </Typography>
        </Stack>
      ))}
      {failed && (
        <Button
          size="small"
          startIcon={<RefreshIcon />}
          onClick={onRetry}
          sx={{ ml: 'auto', fontSize: '0.65rem', py: 0, minWidth: 0 }}
        >
          {t('common.retry')}
        </Button>
      )}
    </Box>
  )
}
