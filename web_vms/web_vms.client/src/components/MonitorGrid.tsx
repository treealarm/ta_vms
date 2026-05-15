import { useMemo, useState } from 'react'
import { Box, IconButton, Menu, MenuItem, Tooltip, Typography } from '@mui/material'
import SwitchCameraIcon from '@mui/icons-material/SwitchCamera'
import type { CameraSetupDto, MonitorDto } from '../dto/dtos'
import { layoutColumns, layoutRows, layoutSlotCount } from '../dto/dtos'
import CameraUnifiedCard from './CameraUnifiedCard'
import { useAppDispatch, useAppSelector } from '../store'
import { setMonitorSlot } from '../store/monitorsSlice'

type Props = {
  monitor: MonitorDto
}

function effectiveCameraForSlot(monitor: MonitorDto, slotIndex: number): string | null {
  const persisted = monitor.slots.find((s) => s.slotIndex === slotIndex)
  if (persisted && monitor.cameraIds.includes(persisted.cameraId)) {
    return persisted.cameraId
  }
  return monitor.cameraIds[slotIndex] ?? null
}

type SlotProps = {
  monitor: MonitorDto
  slotIndex: number
  soloActive: boolean
  canSolo: boolean
  onToggleSolo: () => void
  allCameras: Record<string, CameraSetupDto>
}

function CameraSlot({ monitor, slotIndex, soloActive, canSolo, onToggleSolo, allCameras }: SlotProps) {
  const dispatch = useAppDispatch()
  const [menuAnchor, setMenuAnchor] = useState<HTMLElement | null>(null)

  const activeCameraId = effectiveCameraForSlot(monitor, slotIndex)
  const camera = activeCameraId ? allCameras[activeCameraId] : null

  const handleSelectCamera = (cameraId: string) => {
    setMenuAnchor(null)
    void dispatch(setMonitorSlot({ monitorId: monitor.monitorId, slotIndex, cameraId }))
  }

  return (
    <Box sx={{ position: 'relative', minWidth: 0, minHeight: 0, display: 'flex', overflow: 'hidden' }}>
      {camera ? (
        <CameraUnifiedCard
          camera={camera}
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
                {id}
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

  const slotCount = layoutSlotCount(monitor.layout)
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
          key={slotIndex}
          monitor={monitor}
          slotIndex={slotIndex}
          soloActive={soloSlot === slotIndex}
          canSolo={slotCount > 1}
          onToggleSolo={() => setSoloSlot((s) => (s === slotIndex ? null : slotIndex))}
          allCameras={allCameras}
        />
      ))}
    </Box>
  )
}
