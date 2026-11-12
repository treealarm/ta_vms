import { Box, Stack, Tooltip } from '@mui/material'
import type { MapObjectState, MapStateDescription } from '../../dto/mapDtos'

type Props = {
  state?: MapObjectState
  descriptions: Record<string, MapStateDescription>
}

/**
 * A device part's state as a dot and nothing else: the tree reads, it does not drive. It is the
 * same value the Control panel shows, because both read the map's state for the part's node — a
 * switch in the tree would be a second path to the device and could disagree with the first.
 */
export default function PartStateDot({ state, descriptions }: Props) {
  const names = state?.states ?? []
  if (names.length === 0) return null

  return (
    <Stack direction="row" spacing={0.25} alignItems="center" sx={{ flexShrink: 0 }}>
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
