import { Box, Stack, Tooltip } from '@mui/material'
import { useTranslation } from 'react-i18next'
import type { MapObjectState, MapStateDescription } from '../../dto/mapDtos'
import { FAULT_COLOR } from '../../map/nodeStyle'

type Props = {
  state?: MapObjectState
  descriptions: Record<string, MapStateDescription>
  /** Set only on a device part: false means the device no longer reports it. */
  present?: boolean
}

/**
 * A device part's state as a dot and nothing else: the tree reads, it does not drive. It is the
 * same value the Control panel shows, because both read the map's state for the part's node — a
 * switch in the tree would be a second path to the device and could disagree with the first.
 *
 * Presence is a dot of its own and does not wait for a state or an alarm: a part the device no
 * longer reports has no contact state left, and the row must still say so.
 */
export default function PartStateDot({ state, descriptions, present }: Props) {
  const { t } = useTranslation()
  const names = state?.states ?? []
  if (present !== false && names.length === 0) return null

  return (
    <Stack direction="row" spacing={0.25} alignItems="center" sx={{ flexShrink: 0 }}>
      {present === false ? (
        <Tooltip title={t('maps.node.fault')}>
          <Box
            sx={{
              width: 8,
              height: 8,
              borderRadius: '50%',
              bgcolor: FAULT_COLOR,
            }}
          />
        </Tooltip>
      ) : null}
      {names.map((name) => {
        const description = descriptions[name]
        return (
          <Tooltip key={name} title={description?.stateDescr || name}>
            <Box
              sx={{
                width: 8,
                height: 8,
                borderRadius: '50%',
                bgcolor: description?.stateColor || 'text.disabled',
              }}
            />
          </Tooltip>
        )
      })}
    </Stack>
  )
}
