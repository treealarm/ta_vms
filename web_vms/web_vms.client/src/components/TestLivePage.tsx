import { useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { Box, Button, Stack, TextField, Typography } from '@mui/material'
import CameraStream from './CameraStream'

/**
 * Dev-only page for testing the MediaMTX WHEP proxy in isolation, without logging in.
 * Hits api/live/whep-test/* (anonymous, gated server-side by VMS_LIVE_TEST_PLAYER) instead of
 * the authenticated api/live/whep/* used by the real monitor/preview.
 */
export default function TestLivePage() {
  const [params, setParams] = useSearchParams()
  const cameraId = params.get('cameraId') ?? ''
  const streamName = params.get('streamName') ?? ''
  // This page has no camera config (it runs anonymously), so the router's STUN URL can't be
  // resolved: `?stun=<url>` overrides it, `?stun=` forces LAN-only, absent = public default.
  const stunUrl = params.get('stun')
  const [cameraIdInput, setCameraIdInput] = useState(cameraId)
  const [streamNameInput, setStreamNameInput] = useState(streamName)

  const submit = () => {
    setParams({
      cameraId: cameraIdInput,
      streamName: streamNameInput,
      ...(stunUrl === null ? {} : { stun: stunUrl }),
    })
  }

  return (
    <Box sx={{ p: 2, display: 'flex', flexDirection: 'column', gap: 2, height: '100dvh' }}>
      <Typography variant="h6">WHEP proxy test player</Typography>
      <Stack direction="row" spacing={1} alignItems="center">
        <TextField
          label="cameraId"
          size="small"
          value={cameraIdInput}
          onChange={(e) => setCameraIdInput(e.target.value)}
        />
        <TextField
          label="streamName"
          size="small"
          value={streamNameInput}
          onChange={(e) => setStreamNameInput(e.target.value)}
        />
        <Button variant="contained" onClick={submit} disabled={!cameraIdInput || !streamNameInput}>
          Play
        </Button>
      </Stack>
      {cameraId && streamName && (
        <Box sx={{ flex: 1, minHeight: 0, bgcolor: 'black' }}>
          <CameraStream whepUrl={`/api/live/whep-test/offer/${cameraId}/${streamName}`} stunUrl={stunUrl} />
        </Box>
      )}
    </Box>
  )
}
