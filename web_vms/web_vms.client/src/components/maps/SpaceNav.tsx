import { Box, Breadcrumbs, Button, ButtonGroup, Link, Typography } from '@mui/material'
import type { SpaceDto } from '../../dto/dtos'
import { pathTo, siblingsOf } from './spatialUtils'

type Props = {
  spaces: SpaceDto[]
  space: SpaceDto
  onOpen: (spaceId: string) => void
}

/**
 * Getting around nested spaces, drawn over the map's edges: the way back up on the left, the
 * neighbours — the other floors of the same building — on the right. Down is the footprints on
 * the map itself.
 */
export default function SpaceNav({ spaces, space, onOpen }: Props) {
  const path = pathTo(spaces, space.id ?? '')
  // Top floor first: the order a building is read in.
  const siblings = [...siblingsOf(spaces, space)].reverse()
  return (
    <>
      {path.length > 1 && (
        <Box sx={{ position: 'absolute', top: 10, left: 56, zIndex: 1000, px: 1, py: 0.5, borderRadius: 1, bgcolor: 'rgba(40,40,40,0.85)' }}>
          <Breadcrumbs sx={{ color: '#fff', fontSize: 13 }}>
            {path.map((s) => (s.id === space.id ? (
              <Typography key={s.id} sx={{ color: '#fff', fontSize: 13, fontWeight: 600 }}>{s.name}</Typography>
            ) : (
              <Link key={s.id} component="button" underline="hover" sx={{ color: '#90caf9', fontSize: 13 }} onClick={() => s.id && onOpen(s.id)}>
                {s.name}
              </Link>
            )))}
          </Breadcrumbs>
        </Box>
      )}
      {siblings.length > 1 && (
        <Box sx={{ position: 'absolute', top: 10, right: 10, zIndex: 1000 }}>
          <ButtonGroup orientation="vertical" size="small" variant="contained">
            {siblings.map((s) => (
              <Button
                key={s.id}
                color={s.id === space.id ? 'primary' : 'inherit'}
                onClick={() => s.id && s.id !== space.id && onOpen(s.id)}
                sx={{ minWidth: 72, justifyContent: 'flex-start', textTransform: 'none', ...(s.id !== space.id ? { bgcolor: 'rgba(40,40,40,0.85)', color: '#fff' } : {}) }}
              >
                {s.name}
              </Button>
            ))}
          </ButtonGroup>
        </Box>
      )}
    </>
  )
}
