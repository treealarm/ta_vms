import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { alpha, Box, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'

import { useAppDispatch, useAppSelector, useCameraName } from '../../store'
import { panWindow, seekTo, selectWall, setWindow, zoomWindow } from '../../store/wallPlaybackSlice'
import { wallClockBus } from '../../hooks/wallClockBus'
import { useWallCoverage } from '../../hooks/useWallCoverage'
import { buildTicks, msToX, xToMs } from '../../utils/timelineTicks'
import { DAY_MS, isSameLocalDay } from '../../utils/wallTime'
import type { WallRange } from '../../utils/wallTime'
import WallTimelineTrack from './WallTimelineTrack'
import EventBrackets from './EventBrackets'
import { useWallEvents } from '../../hooks/useWallEvents'

const TICKS_H = 22
const TRACK_H = 16
const ZOOM_STEP = 0.8

type Props = {
  monitorId: string
  /** Cameras currently placed on the wall, for the "all cameras" lane. */
  cameraIds: string[]
  focusedCameraId: string | null
}

export default function WallTimeline({ monitorId, cameraIds, focusedCameraId }: Props) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const wall = useAppSelector(selectWall(monitorId))
  const focusedName = useCameraName(focusedCameraId)

  const rootRef = useRef<HTMLDivElement>(null)
  const playheadRef = useRef<HTMLDivElement>(null)
  const [pxWidth, setPxWidth] = useState(0)

  const { windowStartMs, windowWidthMs, seekMs } = wall
  const win: WallRange = useMemo(
    () => ({ startMs: windowStartMs, endMs: windowStartMs + windowWidthMs }),
    [windowStartMs, windowWidthMs]
  )

  useLayoutEffect(() => {
    const el = rootRef.current
    if (!el) return
    const ro = new ResizeObserver(([entry]) => setPxWidth(entry.contentRect.width))
    ro.observe(el)
    setPxWidth(el.getBoundingClientRect().width)
    return () => ro.disconnect()
  }, [])

  const coverage = useWallCoverage(cameraIds, windowStartMs, windowWidthMs, pxWidth)
  const focusedSegments = useMemo(
    () => (focusedCameraId ? (coverage.byCamera[focusedCameraId] ?? []) : []),
    [focusedCameraId, coverage.byCamera]
  )

  const ticks = useMemo(
    () => buildTicks(win, pxWidth, focusedSegments),
    [win, pxWidth, focusedSegments]
  )

  const events = useWallEvents(focusedCameraId, windowStartMs, windowWidthMs)

  // The playhead is moved by mutating style rather than by re-rendering: it ticks several times a
  // second and this component sits above the whole wall.
  useEffect(() => {
    return wallClockBus.subscribePlayhead((ms) => {
      const el = playheadRef.current
      if (!el) return
      const ratio = (ms - windowStartMs) / windowWidthMs
      el.style.visibility = ratio < 0 || ratio > 1 ? 'hidden' : 'visible'
      el.style.left = `${ratio * 100}%`
    })
  }, [windowStartMs, windowWidthMs])

  // Keep the playhead on screen while playing — but never drag the window back to "today" once the
  // operator has navigated to an older date. A sample more than a day away from the committed seek
  // is a stale clock from a cell that has not caught up yet, and is ignored outright.
  useEffect(() => {
    return wallClockBus.subscribePlayhead((ms) => {
      if (Math.abs(ms - seekMs) > DAY_MS) return
      if (!isSameLocalDay(ms, seekMs)) return
      if (ms >= windowStartMs && ms <= windowStartMs + windowWidthMs) return
      dispatch(setWindow({ monitorId, startMs: ms - windowWidthMs / 2, widthMs: windowWidthMs }))
    })
  }, [dispatch, monitorId, seekMs, windowStartMs, windowWidthMs])

  const msAtClientX = useCallback(
    (clientX: number) => {
      const rect = rootRef.current?.getBoundingClientRect()
      if (!rect || rect.width === 0) return null
      return xToMs(clientX - rect.left, win, rect.width)
    },
    [win]
  )

  const dragRef = useRef<{ startX: number; startWindowMs: number } | null>(null)

  const handlePointerDown = (e: React.PointerEvent<HTMLDivElement>) => {
    // Shift-drag pans; a plain click seeks (handled on pointerup so a drag does not also seek).
    if (!e.shiftKey) return
    dragRef.current = { startX: e.clientX, startWindowMs: windowStartMs }
    e.currentTarget.setPointerCapture(e.pointerId)
  }

  const handlePointerMove = (e: React.PointerEvent<HTMLDivElement>) => {
    const drag = dragRef.current
    const rect = rootRef.current?.getBoundingClientRect()
    if (!drag || !rect || rect.width === 0) return
    const deltaMs = ((drag.startX - e.clientX) / rect.width) * windowWidthMs
    dispatch(setWindow({ monitorId, startMs: drag.startWindowMs + deltaMs, widthMs: windowWidthMs }))
  }

  const handlePointerUp = (e: React.PointerEvent<HTMLDivElement>) => {
    if (dragRef.current) {
      dragRef.current = null
      return
    }
    const target = msAtClientX(e.clientX)
    if (target === null) return
    wallClockBus.reset(target)
    dispatch(seekTo({ monitorId, seekMs: target, arm: true }))
  }

  // Wheel pans, ctrl/cmd+wheel zooms around the cursor. Bound natively because React's wheel
  // listener is passive and cannot preventDefault the page scroll.
  useEffect(() => {
    const el = rootRef.current
    if (!el) return

    const onWheel = (e: WheelEvent) => {
      e.preventDefault()
      if (e.ctrlKey || e.metaKey) {
        const rect = el.getBoundingClientRect()
        const anchorMs = xToMs(e.clientX - rect.left, win, rect.width)
        dispatch(zoomWindow({ monitorId, factor: e.deltaY > 0 ? 1 / ZOOM_STEP : ZOOM_STEP, anchorMs }))
      } else {
        const dir = e.deltaX !== 0 ? e.deltaX : e.deltaY
        dispatch(panWindow({ monitorId, deltaMs: (dir / 500) * windowWidthMs }))
      }
    }

    el.addEventListener('wheel', onWheel, { passive: false })
    return () => el.removeEventListener('wheel', onWheel)
  }, [dispatch, monitorId, win, windowWidthMs])

  return (
    <Box sx={{ userSelect: 'none' }}>
      <Box
        ref={rootRef}
        onPointerDown={handlePointerDown}
        onPointerMove={handlePointerMove}
        onPointerUp={handlePointerUp}
        sx={{ position: 'relative', cursor: 'pointer', touchAction: 'none' }}
      >
        {/* Ruler. No filled band behind it — a grey strip here would sit on top of the tracks. */}
        <Box sx={{ position: 'relative', height: TICKS_H }}>
          {pxWidth > 0 &&
            ticks.map((tick) => {
              const x = msToX(tick.ms, win, pxWidth)
              if (x < 0 || x > pxWidth) return null
              return (
                <Box
                  key={tick.ms}
                  sx={{
                    position: 'absolute',
                    left: `${x}px`,
                    top: 0,
                    bottom: 0,
                    display: 'flex',
                    alignItems: 'flex-end',
                    pointerEvents: 'none',
                  }}
                >
                  <Box
                    sx={{
                      position: 'absolute',
                      bottom: 0,
                      width: tick.kind === 'minor' ? '1px' : '2px',
                      height: tick.kind === 'minor' ? 5 : tick.kind === 'edge' ? 12 : 10,
                      bgcolor: (th) =>
                        tick.kind === 'edge'
                          ? th.palette.warning.main
                          : alpha(th.palette.text.primary, tick.kind === 'major' ? 0.85 : 0.45),
                    }}
                  />
                  {tick.showLabel && (
                    <Typography
                      sx={{
                        position: 'absolute',
                        bottom: 11,
                        left: 3,
                        fontSize: '0.65rem',
                        lineHeight: 1,
                        whiteSpace: 'nowrap',
                        color: tick.showDate ? 'text.primary' : 'text.secondary',
                        fontWeight: tick.showDate ? 700 : 500,
                      }}
                    >
                      {tick.label}
                    </Typography>
                  )}
                </Box>
              )
            })}
        </Box>

        {/* Selected-camera lane. With nothing selected it stays empty and dimmed rather than
            mirroring the lane below — two identical "all cameras" bars say nothing. */}
        <WallTimelineTrack
          label={
            focusedCameraId ? t('wall.selectedCamera', { name: focusedName }) : t('wall.noCameraSelected')
          }
          segments={focusedCameraId ? focusedSegments : []}
          win={win}
          pxWidth={pxWidth}
          height={TRACK_H}
          emphasis
          disabled={!focusedCameraId}
        >
          {focusedCameraId && (
            <EventBrackets
              events={events}
              segments={focusedSegments}
              win={win}
              pxWidth={pxWidth}
              onPick={(ms) => {
                wallClockBus.reset(ms)
                dispatch(seekTo({ monitorId, seekMs: ms, arm: true }))
              }}
            />
          )}
        </WallTimelineTrack>

        <WallTimelineTrack
          label={t('wall.allCameras')}
          segments={coverage.union}
          win={win}
          pxWidth={pxWidth}
          height={TRACK_H}
        />

        <Box
          ref={playheadRef}
          sx={{
            position: 'absolute',
            top: 0,
            bottom: 0,
            width: '2px',
            bgcolor: 'primary.main',
            visibility: 'hidden',
            pointerEvents: 'none',
            zIndex: 2,
          }}
        />
      </Box>
    </Box>
  )
}
