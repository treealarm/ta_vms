import { useSearchParams } from 'react-router-dom'
import { Box, Typography } from '@mui/material'
import CameraStream from './CameraStream'

/**
 * Dev-only page for testing the MediaMTX WHEP proxy in isolation, without logging in.
 * Hits api/live/whep-test/* (anonymous, gated server-side by VMS_LIVE_TEST_PLAYER) instead of
 * the authenticated api/live/whep/* used by the real monitor/preview.
 */
export default function TestLivePage() {
  const [params] = useSearchParams()
  const cameraId = params.get('cameraId') ?? ''
  const streamName = params.get('streamName') ?? ''

  if (!cameraId || !streamName) {
    return (
      <Box sx={{ p: 2 }}>
        <Typography>Usage: /test/live?cameraId=...&amp;streamName=...</Typography>
      </Box>
    )
  }

  const whepUrl = `/api/live/whep-test/offer/${cameraId}/${streamName}`

  return (
    <Box sx={{ width: '100vw', height: '100vh', bgcolor: 'black' }}>
      <CameraStream whepUrl={whepUrl} />
    </Box>
  )
}
