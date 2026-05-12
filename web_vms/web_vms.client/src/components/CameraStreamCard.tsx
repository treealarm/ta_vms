import { useState } from 'react'
import { Box, Tab, Tabs } from '@mui/material'
import type { CameraSetupDto } from '../dto/dtos'
import CameraStream from './CameraStream'

type CameraStreamCardProps = {
  camera: CameraSetupDto
  onStreamChange?: (streamName: string) => void
}

/** Live stream area only; recording and toolbar live in CameraUnifiedCard. */
function CameraStreamCard({ camera, onStreamChange }: CameraStreamCardProps) {
  const webRtcStreams = (camera.streams ?? []).filter((s) => s.webRtc)
  const [selectedIndex, setSelectedIndex] = useState(0)

  const idx = Math.min(selectedIndex, Math.max(0, webRtcStreams.length - 1))

  const streamName = webRtcStreams.length > 0
    ? `${camera.cameraId}/${webRtcStreams[idx].streamName}`
    : camera.cameraId

  const handleTabChange = (_: unknown, v: number) => {
    setSelectedIndex(v)
    onStreamChange?.(webRtcStreams[v]?.streamName ?? '')
  }

  return (
    <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      {webRtcStreams.length > 0 && (
        <Tabs
          value={idx}
          onChange={handleTabChange}
          variant="scrollable"
          scrollButtons="auto"
          sx={{ flexShrink: 0, minHeight: 32, '& .MuiTab-root': { minHeight: 32, py: 0.5 } }}
        >
          {webRtcStreams.map((s) => (
            <Tab key={s.streamName} label={s.streamName} />
          ))}
        </Tabs>
      )}
      <CameraStream streamName={streamName} />
    </Box>
  )
}

export default CameraStreamCard
