import { useEffect, useMemo, useState } from 'react'
import { Box, IconButton, Menu, MenuItem, Tooltip, Typography } from '@mui/material'
import SwitchCameraIcon from '@mui/icons-material/SwitchCamera'
import type { CameraSetupDto, MonitorDto } from '../dto/dtos'
import { layoutColumns, layoutRows, layoutSlotCount } from '../dto/dtos'
import CameraUnifiedCard from './CameraUnifiedCard'
import { useAppDispatch, useAppSelector } from '../store'
import { setMonitorSlot } from '../store/monitorsSlice'
import { selectFocusedSlot, selectWallMode, setFocusedSlot } from '../store/wallPlaybackSlice'
import { wallClockBus } from '../hooks/wallClockBus'

type Props = {
  monitor: MonitorDto
}

function effectiveCameraForSlot(monitor: MonitorDto, slotIndex: number): string | null {
  const persisted = monitor.slots.find((s) => s.slotIndex === slotIndex)
  if (persisted && monitor.cameraIds.includes(persisted.cameraId)) {
    return persisted.cameraId
  }
  return null
}

type SlotProps = {
  monitor: MonitorDto
  slotIndex: number
  soloActive: boolean
  canSolo: boolean
  focused: boolean
  onToggleSolo: () => void
  allCameras: Record<string, CameraSetupDto>
}

function CameraSlot({ monitor, slotIndex, soloActive, canSolo, focused, onToggleSolo, allCameras }: SlotProps) {
  const dispatch = useAppDispatch()
  const [menuAnchor, setMenuAnchor] = useState<HTMLElement | null>(null)
  const wallMode = useAppSelector(selectWallMode(monitor.monitorId))

  const activeCameraId = effectiveCameraForSlot(monitor, slotIndex)
  const camera = activeCameraId ? allCameras[activeCameraId] : null

  const handleSelectCamera = (cameraId: string) => {
    setMenuAnchor(null)
    void dispatch(setMonitorSlot({ monitorId: monitor.monitorId, slotIndex, cameraId }))
  }

  return (
    <Box
      // Selecting a slot only moves focus — it drives the frame and the timeline's track, and never
      // swaps which camera a cell shows.
      onClick={() => dispatch(setFocusedSlot({ monitorId: monitor.monitorId, slotIndex }))}
      // Snapshot and other per-slot lookups address the video through this attribute; indexing the
      // Nth <video> in the DOM breaks as soon as an earlier slot is empty.
      data-slot-index={slotIndex}
      sx={{
        position: 'relative',
        minWidth: 0,
        minHeight: 0,
        display: 'flex',
        borderRadius: 1,
        // Focus must stay distinguishable from the retention indicator, which CameraUnifiedCard
        // draws as a red (palette.error) outline inset by 2px — and in this theme primary is red
        // too. So focus is a light outline drawn OUTSIDE the card, in the grid gap: different
        // colour, different position, and both are visible at once on a recording camera.
        outline: focused ? '2px solid' : 'none',
        outlineColor: 'common.white',
        outlineOffset: '2px',
      }}
    >
      {camera ? (
        <CameraUnifiedCard
          key={camera.cameraId}
          slotKey={`${monitor.monitorId}-${slotIndex}`}
          monitorId={monitor.monitorId}
          camera={camera}
          wallMode={wallMode}
          gridSoloActive={soloActive}
          onToggleGridSolo={canSolo ? onToggleSolo : undefined}
        />
      ) : (
        <Box
          sx={{
            flex: 1,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            border: '1px dashed',
            borderColor: 'divider',
            borderRadius: 1,
          }}
        >
          <Typography variant="body2" color="text.disabled">
            {activeCameraId ? `Camera "${activeCameraId}" not found` : 'Empty slot'}
          </Typography>
        </Box>
      )}

      {monitor.cameraIds.length > 1 && (
        <>
          <Tooltip title="Switch camera" placement="top">
            <IconButton
              size="small"
              onClick={(e) => setMenuAnchor(e.currentTarget)}
              sx={{
                position: 'absolute',
                top: 4,
                right: 4,
                bgcolor: 'background.paper',
                opacity: 0.85,
                '&:hover': { opacity: 1 },
              }}
            >
              <SwitchCameraIcon fontSize="small" />
            </IconButton>
          </Tooltip>
          <Menu
            anchorEl={menuAnchor}
            open={Boolean(menuAnchor)}
            onClose={() => setMenuAnchor(null)}
          >
            {monitor.cameraIds.map((id) => (
              <MenuItem
                key={id}
                selected={id === activeCameraId}
                onClick={() => handleSelectCamera(id)}
              >
                {allCameras[id]?.name || id}
              </MenuItem>
            ))}
          </Menu>
        </>
      )}
    </Box>
  )
}

export default function MonitorGrid({ monitor }: Props) {
  const [soloSlot, setSoloSlot] = useState<number | null>(null)
  const allCameras = useAppSelector((state) => state.live.cameraStatesById)
  const focusedSlot = useAppSelector(selectFocusedSlot(monitor.monitorId))

  const slotCount = layoutSlotCount(monitor.layout)

  // The clock bus prefers the focused cell as playhead owner, so keep it in sync with the wall.
  useEffect(() => {
    wallClockBus.setFocused(focusedSlot === null ? null : `${monitor.monitorId}-${focusedSlot}`)
  }, [monitor.monitorId, focusedSlot])

  // Reset solo when switching monitors or changing layout (stale index would point at non-existent slot)
  useEffect(() => {
    setSoloSlot(null)
  }, [monitor.monitorId, slotCount])
  const cols = layoutColumns(monitor.layout)
  const rows = layoutRows(monitor.layout)

  const slots = useMemo(
    () => Array.from({ length: slotCount }, (_, i) => i),
    [slotCount]
  )

  const visibleSlots = soloSlot !== null ? [soloSlot] : slots

  const gridSx = useMemo(() => {
    const base = {
      display: 'grid',
      gap: 2,
      flex: 1,
      minHeight: 0,
      width: '100%',
      height: '100%',
      alignContent: 'stretch' as const,
    }
    if (soloSlot !== null) {
      return {
        ...base,
        gridTemplateColumns: 'minmax(0, 1fr)',
        gridTemplateRows: 'minmax(0, 1fr)',
      }
    }
    return {
      ...base,
      gridTemplateColumns: `repeat(${cols}, minmax(0, 1fr))`,
      gridTemplateRows: `repeat(${rows}, minmax(0, 1fr))`,
    }
  }, [soloSlot, cols, rows])

  return (
    <Box sx={gridSx}>
      {visibleSlots.map((slotIndex) => (
        <CameraSlot
          key={`${monitor.monitorId}-${slotIndex}`}
          monitor={monitor}
          slotIndex={slotIndex}
          soloActive={soloSlot === slotIndex}
          canSolo={slotCount > 1}
          focused={focusedSlot === slotIndex}
          onToggleSolo={() => setSoloSlot((s) => (s === slotIndex ? null : slotIndex))}
          allCameras={allCameras}
        />
      ))}
    </Box>
  )
}
