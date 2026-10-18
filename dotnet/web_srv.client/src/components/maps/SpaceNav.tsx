import { Box, Breadcrumbs, Button, ButtonGroup, Link, Typography } from '@mui/material'
import type { SpaceDto } from '../../dto/dtos'
import { ALARM_COLOR } from '../../map/cameraStatus'
import { pathTo, siblingsOf } from './spatialUtils'

/** A red dot after a name: something is alarming in that space or inside it. */
const AlarmDot = () => (
  <Box component="span" className="vms-alarm-pulse" sx={{ display: 'inline-block', width: 8, height: 8, ml: 0.75, borderRadius: '50%', bgcolor: ALARM_COLOR }} />
)

type Props = {
  spaces: SpaceDto[]
  space: SpaceDto
  /** Makes the path and the neighbours links. Without it the overlay is only a label naming the
   *  space: the editor picks spaces from its tree, which also holds a building with many plans. */
  onOpen?: (spaceId: string) => void
  /** Spaces with an alarm in them or inside them: the other floors and the way up show a dot. */
  alarmed?: ReadonlySet<string>
}

/**
 * Getting around nested spaces, drawn over the map's edges: the way back up on the left, the
 * neighbours — the other floors of the same building — on the right. Down is the footprints on
 * the map itself. The path is shown for a top-level space too, as its name alone: it is how the
 * map says which space it is.
 */
export default function SpaceNav({ spaces, space, onOpen, alarmed }: Props) {
  const dot = (s: SpaceDto) => (s.id && s.id !== space.id && alarmed?.has(s.id) ? <AlarmDot /> : null)
  const path = pathTo(spaces, space.id ?? '')
  // Top floor first: the order a building is read in.
  const siblings = [...siblingsOf(spaces, space)].reverse()
  return (
    <>
      {path.length > 0 && (
        <Box sx={{ position: 'absolute', top: 10, left: 56, zIndex: 1000, px: 1, py: 0.5, borderRadius: 1, bgcolor: 'rgba(40,40,40,0.85)' }}>
          <Breadcrumbs sx={{ color: '#fff', fontSize: 13 }}>
            {path.map((s) => (s.id === space.id || !onOpen ? (
              <Typography key={s.id} sx={{ color: '#fff', fontSize: 13, fontWeight: s.id === space.id ? 600 : 400 }}>{s.name}</Typography>
            ) : (
              <Link key={s.id} component="button" underline="hover" sx={{ color: '#90caf9', fontSize: 13 }} onClick={() => s.id && onOpen(s.id)}>
                {s.name}{dot(s)}
              </Link>
            )))}
          </Breadcrumbs>
        </Box>
      )}
      {onOpen && siblings.length > 1 && (
        <Box sx={{ position: 'absolute', top: 10, right: 10, zIndex: 1000 }}>
          <ButtonGroup orientation="vertical" size="small" variant="contained">
            {siblings.map((s) => (
              <Button
                key={s.id}
                color={s.id === space.id ? 'primary' : 'inherit'}
                onClick={() => s.id && s.id !== space.id && onOpen(s.id)}
                sx={{ minWidth: 72, justifyContent: 'flex-start', textTransform: 'none', ...(s.id !== space.id ? { bgcolor: 'rgba(40,40,40,0.85)', color: '#fff' } : {}) }}
              >
                {s.name}{dot(s)}
              </Button>
            ))}
          </ButtonGroup>
        </Box>
      )}
    </>
  )
}
