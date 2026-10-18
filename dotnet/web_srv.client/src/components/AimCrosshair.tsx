import { Box } from '@mui/material'

/**
 * The centre of the slave's frame, marked so a spot can be lined up on it precisely.
 *
 * Which point of the slave's picture the object ends up on is decided entirely by how the operator
 * aimed while marking — the binding stores the position, not a framing rule — so "the middle" only
 * means something if the middle is visible. Eyeballing the centre of a moving picture is exactly
 * the sort of small inaccuracy that a survey then bakes in at every distance.
 *
 * The picture is centred in its box by object-fit: contain, so the box centre is the picture centre
 * whatever the stream's aspect ratio, and this needs no letterbox arithmetic of its own.
 */
export default function AimCrosshair() {
  const arm = { position: 'absolute' as const, bgcolor: 'rgba(255,255,255,0.85)', boxShadow: '0 0 2px rgba(0,0,0,0.9)' }
  return (
    <Box sx={{ position: 'absolute', inset: 0, pointerEvents: 'none' }}>
      {/* A gap in the middle, so the crosshair frames the target instead of covering it. */}
      <Box sx={{ ...arm, left: '50%', top: 'calc(50% - 28px)', width: '1px', height: '18px', ml: '-0.5px' }} />
      <Box sx={{ ...arm, left: '50%', top: 'calc(50% + 10px)', width: '1px', height: '18px', ml: '-0.5px' }} />
      <Box sx={{ ...arm, top: '50%', left: 'calc(50% - 28px)', height: '1px', width: '18px', mt: '-0.5px' }} />
      <Box sx={{ ...arm, top: '50%', left: 'calc(50% + 10px)', height: '1px', width: '18px', mt: '-0.5px' }} />
      <Box
        sx={{
          position: 'absolute',
          left: '50%',
          top: '50%',
          width: 20,
          height: 20,
          ml: '-10px',
          mt: '-10px',
          borderRadius: '50%',
          border: '1px solid rgba(255,255,255,0.5)',
        }}
      />
    </Box>
  )
}
