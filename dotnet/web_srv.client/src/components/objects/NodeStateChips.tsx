import { Box, Chip, Stack } from '@mui/material'
import { useTranslation } from 'react-i18next'
import type { MapAlarmState, MapObjectState, MapStateDescription } from '../../dto/mapDtos'
import { ALARM_COLOR, ALARM_STATE } from '../../map/nodeStyle'

type Props = {
  state?: MapObjectState
  alarm?: MapAlarmState
  descriptions: Record<string, MapStateDescription>
}

/**
 * The states a node carries, one chip each in its dictionary's colour, with the alarm first. A
 * replica answers with its owner's states, so the same chips describe a camera on the map and the
 * device's relay inside its diagram. The caller decides what to say when there is nothing to show.
 */
export default function NodeStateChips({ state, alarm, descriptions }: Props) {
  const { t } = useTranslation()
  // The alarm chip already says "Alarm"; the ALARM state chip would repeat it.
  const states = (state?.states ?? []).filter((name) => !(alarm?.alarm && name === ALARM_STATE))

  return (
    <Stack direction="row" spacing={0.5} flexWrap="wrap" useFlexGap>
      {alarm?.alarm ? (
        <Chip size="small" label={t('maps.node.alarm')} sx={{ bgcolor: ALARM_COLOR, color: '#fff' }} />
      ) : null}
      {states.map((name) => {
        const description = descriptions[name]
        return (
          <Chip
            key={name}
            size="small"
            variant="outlined"
            label={description?.stateDescr || name}
            icon={(
              <Box
                component="span"
                sx={{
                  width: 10,
                  height: 10,
                  borderRadius: '50%',
                  bgcolor: description?.stateColor || 'text.disabled',
                }}
              />
            )}
            sx={{ borderColor: description?.stateColor }}
          />
        )
      })}
    </Stack>
  )
}
