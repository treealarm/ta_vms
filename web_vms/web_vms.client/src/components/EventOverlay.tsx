import { Box, Chip, Stack } from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useLiveEvents } from '../hooks/useLiveEvents'

interface Props {
  cameraId: string
  enabled: boolean
}

/**
 * Text banners for what a camera announced about itself, drawn over the live video where boxes go.
 *
 * This is the whole picture for cameras whose analytics cannot produce geometry: a cell-motion or
 * tamper engine reports a condition, not an object, so there is nothing to outline. Rendered as
 * plain React (unlike BboxOverlay's canvas) because events arrive a few times a minute rather than
 * a few times a second — reconciliation is not a cost worth avoiding here.
 *
 * Banners come down on the camera's own clearing edge; the client-side TTL is only the backstop for
 * firmwares that announce a start and then go quiet.
 */
export default function EventOverlay({ cameraId, enabled }: Props) {
  const { t } = useTranslation()
  const events = useLiveEvents(cameraId, enabled)

  if (events.length === 0) return null

  return (
    <Stack
      spacing={0.5}
      sx={{
        position: 'absolute',
        top: 8,
        left: 8,
        // Never intercept clicks: the video underneath owns them (PTZ drag, slot selection).
        pointerEvents: 'none',
        zIndex: 3,
        alignItems: 'flex-start',
      }}
    >
      {events.map((e) => (
        <Chip
          key={e.topic}
          size="small"
          color="warning"
          label={t('cameraCard.onvifEvent', { label: e.label })}
          title={e.topic}
          sx={{ opacity: e.opacity, fontWeight: 600 }}
        />
      ))}
      <Box />
    </Stack>
  )
}
