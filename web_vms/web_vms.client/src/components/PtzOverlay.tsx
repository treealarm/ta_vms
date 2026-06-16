import { useRef, useState } from 'react'
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
import CloseIcon from '@mui/icons-material/Close'
import DragIndicatorIcon from '@mui/icons-material/DragIndicator'

function clamp(value: number, min: number, max: number) {
  return Math.min(Math.max(value, min), Math.max(min, max))
}

type Props = {
  cameraId: string
  profileToken: string
  onClose: () => void
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

export default function PtzOverlay({ cameraId, profileToken, onClose }: Props) {
  const theme = useTheme()
  const movingRef = useRef(false)
  const overlayRef = useRef<HTMLDivElement>(null)
  const dragOffsetRef = useRef<{ x: number; y: number } | null>(null)
  const [pos, setPos] = useState<{ left: number; top: number } | null>(null)

  const btn = (icon: React.ReactNode, title: string, pan = 0, tilt = 0, zoom = 0) => (
    <PtzButton key={title} icon={icon} title={title} pan={pan} tilt={tilt} zoom={zoom}
      cameraId={cameraId} profileToken={profileToken} movingRef={movingRef} />
  )

  const onGripPointerDown = (e: React.PointerEvent<HTMLDivElement>) => {
    const el = overlayRef.current
    const parent = el?.parentElement
    if (!el || !parent) return
    const elRect = el.getBoundingClientRect()
    const parentRect = parent.getBoundingClientRect()
    dragOffsetRef.current = { x: e.clientX - elRect.left, y: e.clientY - elRect.top }
    if (!pos) setPos({ left: elRect.left - parentRect.left, top: elRect.top - parentRect.top })
    e.currentTarget.setPointerCapture(e.pointerId)
  }

  const onGripPointerMove = (e: React.PointerEvent<HTMLDivElement>) => {
    const offset = dragOffsetRef.current
    const el = overlayRef.current
    const parent = el?.parentElement
    if (!offset || !el || !parent) return
    const parentRect = parent.getBoundingClientRect()
    const elRect = el.getBoundingClientRect()
    setPos({
      left: clamp(e.clientX - parentRect.left - offset.x, 0, parentRect.width - elRect.width),
      top: clamp(e.clientY - parentRect.top - offset.y, 0, parentRect.height - elRect.height),
    })
  }

  const onGripPointerUp = (e: React.PointerEvent<HTMLDivElement>) => {
    dragOffsetRef.current = null
    e.currentTarget.releasePointerCapture(e.pointerId)
  }

  return (
    <Box
      ref={overlayRef}
      sx={{
        position: 'absolute',
        zIndex: 10,
        bgcolor: alpha(theme.palette.background.paper, 0.45),
        backdropFilter: 'blur(8px)',
        border: `1px solid ${theme.palette.divider}`,
        borderRadius: `${theme.shape.borderRadius}px`,
        p: 1,
        userSelect: 'none',
        ...(pos
          ? { left: pos.left, top: pos.top }
          : { left: 8, top: '50%', transform: 'translateY(-50%)' }),
      }}
    >
      {/* Drag handle + Close */}
      <Box sx={{ display: 'flex', alignItems: 'center', mb: 0.5 }}>
        <Box
          onPointerDown={onGripPointerDown}
          onPointerMove={onGripPointerMove}
          onPointerUp={onGripPointerUp}
          sx={{
            flex: 1,
            display: 'flex',
            alignItems: 'center',
            color: theme.palette.text.secondary,
            cursor: 'grab',
            touchAction: 'none',
            '&:active': { cursor: 'grabbing' },
          }}
        >
          <DragIndicatorIcon sx={{ fontSize: '1rem' }} />
        </Box>
        <Tooltip title="Close PTZ">
          <IconButton
            size="small"
            onClick={onClose}
            sx={{
              p: '2px',
              color: theme.palette.text.secondary,
              '&:hover': { color: theme.palette.text.primary },
            }}
          >
            <CloseIcon sx={{ fontSize: '0.85rem' }} />
          </IconButton>
        </Tooltip>
      </Box>

      {/* D-pad + zoom */}
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
    </Box>
  )
}
