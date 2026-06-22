import { useRef } from 'react'
import { alpha, Box, IconButton, Tooltip, useTheme } from '@mui/material'
import ArrowUpwardIcon from '@mui/icons-material/ArrowUpward'
import ArrowDownwardIcon from '@mui/icons-material/ArrowDownward'
import ArrowBackIcon from '@mui/icons-material/ArrowBack'
import ArrowForwardIcon from '@mui/icons-material/ArrowForward'
import NorthWestIcon from '@mui/icons-material/NorthWest'
import NorthEastIcon from '@mui/icons-material/NorthEast'
import SouthWestIcon from '@mui/icons-material/SouthWest'
import SouthEastIcon from '@mui/icons-material/SouthEast'
import AddIcon from '@mui/icons-material/Add'
import RemoveIcon from '@mui/icons-material/Remove'

type Props = {
  cameraId: string
  profileToken: string
}

const SPEED = 0.5

async function ptzMove(cameraId: string, profileToken: string, pan: number, tilt: number, zoom: number) {
  await fetch('/api/ptz/continuous-move', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ cameraId, profileToken, pan, tilt, zoom }),
  })
}

async function ptzStop(cameraId: string, profileToken: string) {
  await fetch('/api/ptz/stop', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ cameraId, profileToken }),
  })
}

// Backend ContinuousMove timeout is PT3S; repeat every 2s so camera never auto-stops between refreshes
const MOVE_REPEAT_MS = 2000

function PtzButton({
  icon, title, pan = 0, tilt = 0, zoom = 0,
  cameraId, profileToken, movingRef,
}: {
  icon: React.ReactNode
  title: string
  pan?: number
  tilt?: number
  zoom?: number
  cameraId: string
  profileToken: string
  movingRef: { current: boolean }
}) {
  const theme = useTheme()
  const intervalRef = useRef<number | null>(null)

  const start = () => {
    if (movingRef.current) return
    movingRef.current = true
    void ptzMove(cameraId, profileToken, pan * SPEED, tilt * SPEED, zoom * SPEED)
    intervalRef.current = window.setInterval(() => {
      if (movingRef.current)
        void ptzMove(cameraId, profileToken, pan * SPEED, tilt * SPEED, zoom * SPEED)
    }, MOVE_REPEAT_MS)
  }
  const stop = () => {
    if (!movingRef.current) return
    movingRef.current = false
    if (intervalRef.current !== null) { window.clearInterval(intervalRef.current); intervalRef.current = null }
    void ptzStop(cameraId, profileToken)
  }

  return (
    <Tooltip title={title} placement="top">
      <IconButton
        size="small"
        sx={{
          width: 36,
          height: 36,
          color: theme.palette.text.primary,
          bgcolor: alpha(theme.palette.secondary.main, 0.25),
          borderRadius: `${(theme.shape.borderRadius as number) / 2}px`,
          border: `1px solid ${alpha(theme.palette.divider, 0.6)}`,
          '&:hover': {
            bgcolor: alpha(theme.palette.secondary.light, 0.35),
          },
          '&:active': {
            bgcolor: alpha(theme.palette.primary.main, 0.55),
            color: '#fff',
          },
        }}
        onMouseDown={start}
        onMouseUp={stop}
        onMouseLeave={stop}
        onTouchStart={(e) => { e.preventDefault(); start() }}
        onTouchEnd={(e) => { e.preventDefault(); stop() }}
      >
        {icon}
      </IconButton>
    </Tooltip>
  )
}

// Plain (non-floating) PTZ D-pad + zoom control — shared by the monitor's draggable PtzOverlay
// and any inline placement (e.g. the ONVIF settings panel) that just wants the buttons in flow.
export default function PtzControlPad({ cameraId, profileToken }: Props) {
  const movingRef = useRef(false)

  const btn = (icon: React.ReactNode, title: string, pan = 0, tilt = 0, zoom = 0) => (
    <PtzButton key={title} icon={icon} title={title} pan={pan} tilt={tilt} zoom={zoom}
      cameraId={cameraId} profileToken={profileToken} movingRef={movingRef} />
  )

  return (
    <Box sx={{ display: 'flex', gap: 1, alignItems: 'center' }}>
      {/* D-pad 3×3 with diagonals */}
      <Box sx={{
        display: 'grid',
        gridTemplateColumns: 'repeat(3, 36px)',
        gridTemplateRows: 'repeat(3, 36px)',
        gap: '3px',
      }}>
        {btn(<NorthWestIcon fontSize="small" />, 'Up-Left',   -1,  1)}
        {btn(<ArrowUpwardIcon fontSize="small" />,   'Up',     0,  1)}
        {btn(<NorthEastIcon fontSize="small" />, 'Up-Right',   1,  1)}
        {btn(<ArrowBackIcon fontSize="small" />,    'Left',   -1,  0)}
        <Box />
        {btn(<ArrowForwardIcon fontSize="small" />, 'Right',   1,  0)}
        {btn(<SouthWestIcon fontSize="small" />, 'Down-Left', -1, -1)}
        {btn(<ArrowDownwardIcon fontSize="small" />, 'Down',   0, -1)}
        {btn(<SouthEastIcon fontSize="small" />, 'Down-Right', 1, -1)}
      </Box>

      {/* Zoom column */}
      <Box sx={{ display: 'flex', flexDirection: 'column', gap: '3px' }}>
        {btn(<AddIcon fontSize="small" />, 'Zoom in', 0, 0, 1)}
        {btn(<RemoveIcon fontSize="small" />, 'Zoom out', 0, 0, -1)}
      </Box>
    </Box>
  )
}
