import { Box } from '@mui/material'
import type { CameraSetupDto } from '../dto/dtos'
import CameraStream from './CameraStream'

type CameraStreamCardProps = {
  camera: CameraSetupDto
}

/** Live stream area only; recording and toolbar live in CameraUnifiedCard. */
function CameraStreamCard({ camera }: CameraStreamCardProps) {
  return (
    <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <CameraStream streamName={camera.cameraId} />
    </Box>
  )
}

export default CameraStreamCard
