import { useEffect, useMemo } from 'react'
import { Box } from '@mui/material'

import { useAppDispatch, useAppSelector } from '../../store'
import { clearInitialJump, selectWall, seekTo, setWindow } from '../../store/wallPlaybackSlice'
import { useWallCoverage } from '../../hooks/useWallCoverage'
import { wallClockBus } from '../../hooks/wallClockBus'
import { fitWindowToDay, lastCoveredMs, segmentAt } from '../../utils/archiveRanges'
import { DAY_MS, MINUTE_MS } from '../../utils/wallTime'
import type { MonitorDto } from '../../dto/dtos'
import WallPlaybackBar from './WallPlaybackBar'
import WallTimeline from './WallTimeline'

// Day navigation needs to see far beyond the visible window — the previous day with archive may be
// weeks back. This is a separate, deliberately coarse query: a wide span with a large merge
// tolerance collapses to a handful of intervals, so it stays cheap.
const DAY_NAV_SPAN_MS = 90 * DAY_MS
const DAY_NAV_PSEUDO_WIDTH = 1200

// Floor for the fitted startup window, so a day holding a single short clip does not open zoomed
// down to seconds.
const MIN_STARTUP_WINDOW_MS = 30 * MINUTE_MS

function cameraForSlot(monitor: MonitorDto, slotIndex: number | null): string | null {
  if (slotIndex === null) return null
  const slot = monitor.slots.find((s) => s.slotIndex === slotIndex)
  return slot && monitor.cameraIds.includes(slot.cameraId) ? slot.cameraId : null
}

export default function WallPlaybackPanel({ monitor }: { monitor: MonitorDto }) {
  const monitorId = monitor.monitorId
  const dispatch = useAppDispatch()
  const wall = useAppSelector(selectWall(monitorId))

  const focusedCameraId = cameraForSlot(monitor, wall.focusedSlotIndex)

  // Cameras actually placed in slots — not monitor.cameraIds, which also holds cameras that are
  // merely available to the wall.
  const cameraIds = useMemo(() => {
    const ids = monitor.slots
      .map((s) => s.cameraId)
      .filter((id) => monitor.cameraIds.includes(id))
    return [...new Set(ids)]
  }, [monitor.slots, monitor.cameraIds])

  // Empty in live mode so the wall costs nothing extra until playback is actually entered.
  const dayNavCameras = useMemo(
    () => (wall.mode !== 'playback' ? [] : focusedCameraId ? [focusedCameraId] : cameraIds),
    [wall.mode, focusedCameraId, cameraIds]
  )

  const dayNav = useWallCoverage(
    dayNavCameras,
    wall.seekMs - DAY_NAV_SPAN_MS / 2,
    DAY_NAV_SPAN_MS,
    DAY_NAV_PSEUDO_WIDTH
  )

  const focusedSegments = useMemo(
    () => (focusedCameraId ? (dayNav.byCamera[focusedCameraId] ?? []) : dayNav.union),
    [focusedCameraId, dayNav.byCamera, dayNav.union]
  )

  // Entering playback parks the wall at "now", which on most installations is hours past the last
  // recording — the operator would be looking at an empty timeline. Once, on entry, land on the
  // latest archive instead. Only once: after this, focusing a camera that has nothing today must
  // not move the wall's day, it must just say "no recording at this time".
  useEffect(() => {
    if (wall.mode !== 'playback' || !wall.initialJumpPending) return
    if (dayNav.loading || focusedSegments.length === 0) return

    if (segmentAt(focusedSegments, wall.seekMs)) {
      dispatch(clearInitialJump({ monitorId }))
      return
    }

    const target = lastCoveredMs(focusedSegments)
    if (target === null) {
      dispatch(clearInitialJump({ monitorId }))
      return
    }

    wallClockBus.reset(target)
    dispatch(seekTo({ monitorId, seekMs: target, arm: false }))
    // Recenter explicitly. A plain seek deliberately leaves the window alone (rescrolling under
    // the operator on every click would be maddening), but an automatic jump must bring its
    // destination into view — framed on the day's actual recordings rather than on 24 hours of
    // mostly-empty time.
    const fit = fitWindowToDay(focusedSegments, target, { minWidthMs: MIN_STARTUP_WINDOW_MS })
    dispatch(setWindow({ monitorId, startMs: fit.startMs, widthMs: fit.widthMs }))
  }, [dispatch, monitorId, wall.mode, wall.initialJumpPending, wall.seekMs, dayNav.loading, focusedSegments])

  return (
    <Box sx={{ flexShrink: 0, pt: 1 }}>
      <WallPlaybackBar
        monitorId={monitorId}
        focusedSegments={focusedSegments}
        focusedCameraId={focusedCameraId}
      />
      {wall.mode === 'playback' && (
        <WallTimeline
          monitorId={monitorId}
          cameraIds={cameraIds}
          focusedCameraId={focusedCameraId}
        />
      )}
    </Box>
  )
}
