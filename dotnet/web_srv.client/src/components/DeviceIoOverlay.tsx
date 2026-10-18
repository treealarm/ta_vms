import { useRef, useState } from 'react'
import { alpha, Box, IconButton, Tooltip, useTheme } from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import DragIndicatorIcon from '@mui/icons-material/DragIndicator'
import { useTranslation } from 'react-i18next'
import DeviceIoPanel from './DeviceIoPanel'

function clamp(value: number, min: number, max: number) {
  return Math.min(Math.max(value, min), Math.max(min, max))
}

type Props = {
  cameraId: string
  onClose: () => void
}

export default function DeviceIoOverlay({ cameraId, onClose }: Props) {
  const { t } = useTranslation()
  const theme = useTheme()
  const overlayRef = useRef<HTMLDivElement>(null)
  const dragOffsetRef = useRef<{ x: number; y: number } | null>(null)
  const [pos, setPos] = useState<{ left: number; top: number } | null>(null)

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
      // Dragged around the video by its own grip — keep the monitor grid from reading that as
      // dragging the whole tile to another cell (see NO_TILE_DRAG in MonitorGrid).
      data-nodrag
      sx={{
        position: 'absolute',
        zIndex: 10,
        bgcolor: alpha(theme.palette.background.paper, 0.45),
        backdropFilter: 'blur(8px)',
        border: `1px solid ${theme.palette.divider}`,
        borderRadius: `${theme.shape.borderRadius}px`,
        p: 1,
        userSelect: 'none',
        // Default near the trigger buttons on the left icon strip (same as PtzOverlay). PTZ and
        // Device I/O are mutually exclusive, so they never render on top of each other anyway.
        ...(pos
          ? { left: pos.left, top: pos.top }
          : { left: 8, top: '50%', transform: 'translateY(-50%)' }),
      }}
    >
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
        <Tooltip title={t('cameraCard.deviceIoClose')}>
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

      <DeviceIoPanel cameraId={cameraId} />
    </Box>
  )
}
