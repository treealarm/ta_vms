import { useEffect, useMemo, useState } from 'react'
import { Box, IconButton, Menu, MenuItem, Tooltip, Typography } from '@mui/material'
import SwitchCameraIcon from '@mui/icons-material/SwitchCamera'
import { useTranslation } from 'react-i18next'
import type { CameraSetupDto, MonitorDto } from '../dto/dtos'
import { layoutColumns, layoutFeatureSpan, layoutRows, layoutSlotCount } from '../dto/dtos'
import CameraUnifiedCard from './CameraUnifiedCard'
import { useAppDispatch, useAppSelector } from '../store'
import { clearMonitorSlot, setMonitorCameras, setMonitorSlot } from '../store/monitorsSlice'
import { selectFocusedSlot, selectWall, setFocusedSlot } from '../store/wallPlaybackSlice'
import { wallClockBus } from '../hooks/wallClockBus'

type Props = {
  monitor: MonitorDto
  /**
   * Investigation boards draw from every camera in the system rather than from a pre-assigned
   * pool: the point of a board is that its camera set is discovered while working, not planned.
   */
  openCameraPool?: boolean
}

// Descendants that must keep the pointer for themselves instead of starting a tile drag.
// MUI's Select renders a div with role="combobox", not a <select>, so it needs naming explicitly.
const NO_TILE_DRAG = 'button, [role="button"], [role="combobox"], input, select, [data-nodrag]'

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
  /** Every camera on the wall — cells share one coverage query keyed on this list. */
  wallCameraIds: string[]
  openCameraPool: boolean
  /** Rows and columns this cell occupies in a feature layout; 0 for an ordinary one-cell slot. */
  span: number
  soloActive: boolean
  canSolo: boolean
  focused: boolean
  onToggleSolo: () => void
  allCameras: Record<string, CameraSetupDto>
  dragOver: boolean
  onDragSlotStart: () => void
  onDragSlotEnd: () => void
  onDragSlotOver: () => void
  onDropSlot: () => void
}

function CameraSlot({
  monitor, slotIndex, wallCameraIds, openCameraPool, span, soloActive, canSolo, focused,
  onToggleSolo, allCameras, dragOver, onDragSlotStart, onDragSlotEnd, onDragSlotOver, onDropSlot,
}: SlotProps) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const [menuAnchor, setMenuAnchor] = useState<HTMLElement | null>(null)
  const wall = useAppSelector(selectWall(monitor.monitorId))

  const activeCameraId = effectiveCameraForSlot(monitor, slotIndex)
  const camera = activeCameraId ? allCameras[activeCameraId] : null

  const selectableCameraIds = openCameraPool ? Object.keys(allCameras).sort() : monitor.cameraIds

  const handleSelectCamera = async (cameraId: string) => {
    setMenuAnchor(null)
    // A camera picked from outside the pool has to join it first — the pool is what the wall's
    // coverage query and the "all cameras" timeline lane are built from.
    if (!monitor.cameraIds.includes(cameraId)) {
      await dispatch(setMonitorCameras({
        monitorId: monitor.monitorId,
        cameraIds: [...monitor.cameraIds, cameraId],
      }))
    }
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
      draggable={camera !== null}
      onDragStart={(e) => {
        // The cell is the drag handle, but it is full of things that own the pointer themselves —
        // the control strip's buttons, and the PTZ / device-I/O overlays, which are dragged around
        // the video by their own grips. Starting a tile drag from any of those would make them
        // unusable, so the gesture is refused there and the child keeps the pointer.
        if ((e.target as HTMLElement).closest?.(NO_TILE_DRAG)) {
          e.preventDefault()
          return
        }
        // Firefox ignores dragstart unless some data is set.
        e.dataTransfer.setData('text/plain', String(slotIndex))
        e.dataTransfer.effectAllowed = 'move'
        onDragSlotStart()
      }}
      onDragEnd={onDragSlotEnd}
      onDragOver={(e) => {
        // preventDefault is what marks an element as a drop target at all.
        e.preventDefault()
        e.dataTransfer.dropEffect = 'move'
        onDragSlotOver()
      }}
      onDrop={(e) => {
        e.preventDefault()
        onDropSlot()
      }}
      sx={{
        position: 'relative',
        minWidth: 0,
        minHeight: 0,
        display: 'flex',
        borderRadius: 1,
        ...(span > 0 ? { gridColumn: `span ${span}`, gridRow: `span ${span}` } : {}),
        ...(dragOver
          ? { boxShadow: (th) => `inset 0 0 0 3px ${th.palette.primary.main}`, opacity: 0.75 }
          : {}),
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
          slotIndex={slotIndex}
          monitorKind={monitor.kind}
          camera={camera}
          wallMode={wall.mode}
          wallCameraIds={wallCameraIds}
          coverageWindowStartMs={wall.windowStartMs}
          coverageWindowWidthMs={wall.windowWidthMs}
          gridSoloActive={soloActive}
          onToggleGridSolo={canSolo ? onToggleSolo : undefined}
        />
      ) : (
        <Box
          // On a board an empty cell is the way to add a camera, so the whole cell is the target
          // rather than a small icon in its corner.
          onClick={openCameraPool ? (e) => setMenuAnchor(e.currentTarget) : undefined}
          sx={{
            flex: 1,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            border: '1px dashed',
            borderColor: 'divider',
            borderRadius: 1,
            ...(openCameraPool && !activeCameraId
              ? { cursor: 'pointer', '&:hover': { borderColor: 'text.secondary' } }
              : {}),
          }}
        >
          <Typography variant="body2" color="text.disabled">
            {activeCameraId
              ? `Camera "${activeCameraId}" not found`
              : openCameraPool ? t('monitors.addCamera') : 'Empty slot'}
          </Typography>
        </Box>
      )}

      {selectableCameraIds.length > 1 && camera && (
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
      )}

      <Menu
        anchorEl={menuAnchor}
        open={Boolean(menuAnchor)}
        onClose={() => setMenuAnchor(null)}
      >
        {selectableCameraIds.map((id) => (
          <MenuItem
            key={id}
            selected={id === activeCameraId}
            onClick={() => void handleSelectCamera(id)}
          >
            {allCameras[id]?.name || id}
          </MenuItem>
        ))}
      </Menu>
    </Box>
  )
}

export default function MonitorGrid({ monitor, openCameraPool = false }: Props) {
  const dispatch = useAppDispatch()
  const [soloSlot, setSoloSlot] = useState<number | null>(null)
  const [drag, setDrag] = useState<{ from: number; over: number | null } | null>(null)
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
  const featureSpan = layoutFeatureSpan(monitor.layout)

  const slots = useMemo(
    () => Array.from({ length: slotCount }, (_, i) => i),
    [slotCount]
  )

  // Cameras actually placed in slots, shared by every cell so their coverage query collapses into
  // a single batched request instead of one per cell.
  const wallCameraIds = useMemo(
    () => [...new Set(monitor.slots.map((s) => s.cameraId).filter((id) => monitor.cameraIds.includes(id)))],
    [monitor.slots, monitor.cameraIds]
  )

  const visibleSlots = soloSlot !== null ? [soloSlot] : slots

  /**
   * Rearranges the wall by dropping one cell onto another: the two cameras trade places, or the
   * camera moves when the target cell is empty.
   *
   * Both cells are written even for a move — the vacated one has to be cleared, not left holding a
   * stale row, which is why clearing a slot exists as its own operation.
   */
  const handleDrop = (from: number, to: number) => {
    setDrag(null)
    if (from === to) return

    const monitorId = monitor.monitorId
    const fromCamera = effectiveCameraForSlot(monitor, from)
    const toCamera = effectiveCameraForSlot(monitor, to)
    if (!fromCamera) return

    void dispatch(setMonitorSlot({ monitorId, slotIndex: to, cameraId: fromCamera }))
    if (toCamera) {
      void dispatch(setMonitorSlot({ monitorId, slotIndex: from, cameraId: toCamera }))
    } else {
      void dispatch(clearMonitorSlot({ monitorId, slotIndex: from }))
    }

    // Focus follows the camera the operator was dragging, not the cell it used to sit in.
    if (focusedSlot === from) dispatch(setFocusedSlot({ monitorId, slotIndex: to }))
    else if (focusedSlot === to) dispatch(setFocusedSlot({ monitorId, slotIndex: from }))
  }

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
          wallCameraIds={wallCameraIds}
          openCameraPool={openCameraPool}
          // Only the feature cell is placed by hand; the rest auto-fill around it. Suppressed in
          // solo, where the grid is a single cell and a span would overflow it.
          span={soloSlot === null && slotIndex === 0 ? featureSpan : 0}
          soloActive={soloSlot === slotIndex}
          canSolo={slotCount > 1}
          focused={focusedSlot === slotIndex}
          onToggleSolo={() => setSoloSlot((s) => (s === slotIndex ? null : slotIndex))}
          allCameras={allCameras}
          dragOver={drag !== null && drag.over === slotIndex && drag.from !== slotIndex}
          onDragSlotStart={() => setDrag({ from: slotIndex, over: null })}
          onDragSlotEnd={() => setDrag(null)}
          onDragSlotOver={() => setDrag((d) => (d && d.over !== slotIndex ? { ...d, over: slotIndex } : d))}
          onDropSlot={() => drag && handleDrop(drag.from, slotIndex)}
        />
      ))}
    </Box>
  )
}
