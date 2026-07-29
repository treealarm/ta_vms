import { useEffect, useMemo, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Button, Menu, MenuItem, Tooltip } from '@mui/material'
import PlayCircleOutlineIcon from '@mui/icons-material/PlayCircleOutline'
import { useTranslation } from 'react-i18next'

import type { EpisodeDto, MonitorDto } from '../dto/dtos'
import { layoutSlotCount, nextLayoutUp } from '../dto/dtos'
import { useAppDispatch, useAppSelector } from '../store'
import { loadMonitors, setMonitorCameras, setMonitorSlot, upsertMonitor } from '../store/monitorsSlice'

/** Slot the camera already sits in, or the first free one — null when the grid is full. */
function freeSlotIndex(monitor: MonitorDto, cameraId: string): number | null {
  const placed = monitor.slots.find((s) => s.cameraId === cameraId)
  if (placed) return placed.slotIndex

  const count = layoutSlotCount(monitor.layout)
  const taken = new Set(monitor.slots.filter((s) => s.slotIndex < count).map((s) => s.slotIndex))
  for (let i = 0; i < count; i += 1) {
    if (!taken.has(i)) return i
  }
  return null
}

/**
 * Takes an event to the wall it can be reviewed on: a fresh investigation board, an existing one,
 * or — when the camera already hangs on a shared monitor — that monitor. The destination is
 * addressed by URL (?monitorId&cameraId&seekMs) so the receiving view owns the seek, rather than
 * this component reaching into another wall's playback state.
 */
export default function EventOpenInPlayback({ episode }: { episode: EpisodeDto | null }) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const navigate = useNavigate()
  const monitors = useAppSelector((s) => s.monitors.monitors)
  const [anchor, setAnchor] = useState<HTMLElement | null>(null)

  useEffect(() => {
    void dispatch(loadMonitors())
  }, [dispatch])

  const boards = useMemo(() => monitors.filter((m) => m.kind === 'investigation'), [monitors])
  const walls = useMemo(
    () => monitors.filter((m) => m.kind === 'wall' && m.slots.some((s) => s.cameraId === episode?.cameraId)),
    [monitors, episode?.cameraId]
  )

  if (!episode) return null

  const cameraId = episode.cameraId
  const seekMs = Date.parse(episode.alarmStartTs)

  const go = (path: string, monitorId: string) => {
    const qs = new URLSearchParams({ monitorId, cameraId, seekMs: String(seekMs) })
    navigate(`${path}?${qs.toString()}`)
  }

  const handleNewBoard = async () => {
    setAnchor(null)
    const used = new Set(boards.map((b) => b.name))
    let index = 1
    while (used.has(t('monitors.boardName', { index }))) index += 1

    const created = await dispatch(upsertMonitor({
      monitorId: '',
      name: t('monitors.boardName', { index }),
      layout: '1x1',
      cameraIds: [],
      slots: [],
      kind: 'investigation',
      owner: '',
    }))
    if (!upsertMonitor.fulfilled.match(created)) return

    const boardId = created.payload.monitorId
    await dispatch(setMonitorCameras({ monitorId: boardId, cameraIds: [cameraId] }))
    await dispatch(setMonitorSlot({ monitorId: boardId, slotIndex: 0, cameraId }))
    go('/operator/investigations', boardId)
  }

  const handleAddToBoard = async (board: MonitorDto) => {
    setAnchor(null)

    // A full grid grows one step rather than evicting a camera the operator put there on purpose.
    let target = board
    if (freeSlotIndex(board, cameraId) === null) {
      const grown = nextLayoutUp(board.layout)
      if (!grown) return
      const result = await dispatch(upsertMonitor({ ...board, layout: grown }))
      if (!upsertMonitor.fulfilled.match(result)) return
      target = { ...board, layout: grown }
    }

    const slotIndex = freeSlotIndex(target, cameraId)
    if (slotIndex === null) return

    if (!target.cameraIds.includes(cameraId)) {
      await dispatch(setMonitorCameras({
        monitorId: target.monitorId,
        cameraIds: [...target.cameraIds, cameraId],
      }))
    }
    await dispatch(setMonitorSlot({ monitorId: target.monitorId, slotIndex, cameraId }))
    go('/operator/investigations', target.monitorId)
  }

  return (
    <>
      <Button
        size="small"
        variant="outlined"
        startIcon={<PlayCircleOutlineIcon />}
        onClick={(e) => setAnchor(e.currentTarget)}
      >
        {t('wall.openOnMonitor')}
      </Button>

      <Menu anchorEl={anchor} open={Boolean(anchor)} onClose={() => setAnchor(null)}>
        <MenuItem onClick={() => void handleNewBoard()}>{t('wall.newBoardWithCamera')}</MenuItem>

        {boards.map((b) => {
          const full = freeSlotIndex(b, cameraId) === null && nextLayoutUp(b.layout) === null
          return (
            <Tooltip key={b.monitorId} title={full ? t('wall.boardFull') : ''} placement="left">
              <span>
                <MenuItem disabled={full} onClick={() => void handleAddToBoard(b)}>
                  {t('wall.addToBoard', { name: b.name })}
                </MenuItem>
              </span>
            </Tooltip>
          )
        })}

        {walls.map((w) => (
          <MenuItem key={w.monitorId} onClick={() => { setAnchor(null); go('/operator/monitors', w.monitorId) }}>
            {t('wall.openOnWall', { name: w.name })}
          </MenuItem>
        ))}
      </Menu>
    </>
  )
}
