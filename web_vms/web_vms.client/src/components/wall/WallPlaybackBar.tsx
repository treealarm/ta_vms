import { useMemo, useState } from 'react'
import {
  Box,
  Chip,
  IconButton,
  Popover,
  Stack,
  ToggleButton,
  ToggleButtonGroup,
  Tooltip,
} from '@mui/material'
import PlayArrowIcon from '@mui/icons-material/PlayArrow'
import PauseIcon from '@mui/icons-material/Pause'
import Replay30Icon from '@mui/icons-material/Replay30'
import Forward30Icon from '@mui/icons-material/Forward30'
import ZoomInIcon from '@mui/icons-material/ZoomIn'
import ZoomOutIcon from '@mui/icons-material/ZoomOut'
import ZoomOutMapIcon from '@mui/icons-material/ZoomOutMap'
import ChevronLeftIcon from '@mui/icons-material/ChevronLeft'
import ChevronRightIcon from '@mui/icons-material/ChevronRight'
import EventIcon from '@mui/icons-material/Event'
import KeyboardDoubleArrowLeftIcon from '@mui/icons-material/KeyboardDoubleArrowLeft'
import KeyboardDoubleArrowRightIcon from '@mui/icons-material/KeyboardDoubleArrowRight'
import { DateTimePicker } from '@mui/x-date-pickers/DateTimePicker'
import { LocalizationProvider } from '@mui/x-date-pickers/LocalizationProvider'
import { AdapterDayjs } from '@mui/x-date-pickers/AdapterDayjs'
import dayjs from 'dayjs'
import { useTranslation } from 'react-i18next'

import { useAppDispatch, useAppSelector } from '../../store'
import {
  enterPlayback,
  exitPlayback,
  jumpToDay,
  nudgeSeek,
  resetZoom,
  seekTo,
  selectWall,
  setArmed,
  setWindow,
  zoomWindow,
  type WallMode,
} from '../../store/wallPlaybackSlice'
import { wallClockBus } from '../../hooks/wallClockBus'
import { adjacentCoveredDay, coveredDayKeys } from '../../utils/adjacentDay'
import { panWindowToCoverage } from '../../utils/archiveRanges'
import { dayKeyLocal, dayKeyToLocalMidnightMs } from '../../utils/wallTime'
import type { Segment } from '../../utils/archiveRanges'
import PlayheadClock from './PlayheadClock'

const NUDGE_MS = 30_000
const ZOOM_STEP = 0.5

type Props = {
  monitorId: string
  /** Coverage of the focused camera — day navigation follows the camera the operator is watching. */
  focusedSegments: readonly Segment[]
}

export default function WallPlaybackBar({ monitorId, focusedSegments }: Props) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const wall = useAppSelector(selectWall(monitorId))
  const [pickerAnchor, setPickerAnchor] = useState<HTMLElement | null>(null)

  const dayKeys = useMemo(() => coveredDayKeys(focusedSegments), [focusedSegments])

  const handleMode = (_e: React.MouseEvent<HTMLElement>, next: WallMode | null) => {
    if (!next || next === wall.mode) return
    if (next === 'playback') dispatch(enterPlayback({ monitorId }))
    else dispatch(exitPlayback({ monitorId }))
  }

  const handleNudge = (deltaMs: number) => {
    // Move the shared playhead first, so cells do not briefly follow the stale position.
    wallClockBus.reset(wall.seekMs + deltaMs)
    dispatch(nudgeSeek({ monitorId, deltaMs }))
  }

  // Steps to the next day that actually holds archive, skipping empty ones however many there are,
  // and lands on its local midnight without arming playback.
  const handleDayStep = (dir: -1 | 1) => {
    const key = adjacentCoveredDay(dayKeys, dayKeyLocal(wall.seekMs), dir)
    if (!key) return
    const dayStartMs = dayKeyToLocalMidnightMs(key)
    if (!Number.isFinite(dayStartMs)) return
    wallClockBus.reset(dayStartMs)
    dispatch(jumpToDay({ monitorId, dayStartMs }))
  }

  const canStepBack = adjacentCoveredDay(dayKeys, dayKeyLocal(wall.seekMs), -1) !== null
  const canStepFwd = adjacentCoveredDay(dayKeys, dayKeyLocal(wall.seekMs), 1) !== null

  const panBackTarget = panWindowToCoverage(
    focusedSegments, wall.windowStartMs, wall.windowWidthMs, -wall.windowWidthMs / 2)
  const panFwdTarget = panWindowToCoverage(
    focusedSegments, wall.windowStartMs, wall.windowWidthMs, wall.windowWidthMs / 2)

  const handlePan = (dir: -1 | 1) => {
    const target = dir < 0 ? panBackTarget : panFwdTarget
    if (target === null) return
    dispatch(setWindow({ monitorId, startMs: target, widthMs: wall.windowWidthMs }))
  }

  const playback = wall.mode === 'playback'

  return (
    <Stack direction="row" alignItems="center" spacing={0.5} sx={{ px: 0.5, py: 0.5 }}>
      <ToggleButtonGroup size="small" exclusive value={wall.mode} onChange={handleMode}>
        <ToggleButton value="live" sx={{ px: 1.25, py: 0.25, fontSize: '0.7rem' }}>
          {t('wall.modeLive')}
        </ToggleButton>
        <ToggleButton value="playback" sx={{ px: 1.25, py: 0.25, fontSize: '0.7rem' }}>
          {t('wall.modePlayback')}
        </ToggleButton>
      </ToggleButtonGroup>

      {playback && (
        <>
          <Chip size="small" color="primary" variant="outlined" label={t('wall.badgePlayback')} />

          <Tooltip title={t('wall.prevDay')}>
            <span>
              <IconButton size="small" disabled={!canStepBack} onClick={() => handleDayStep(-1)}>
                <ChevronLeftIcon fontSize="small" />
              </IconButton>
            </span>
          </Tooltip>

          <Tooltip title={t('wall.back30')}>
            <IconButton size="small" onClick={() => handleNudge(-NUDGE_MS)}>
              <Replay30Icon fontSize="small" />
            </IconButton>
          </Tooltip>

          <Tooltip title={wall.armed ? t('wall.pause') : t('wall.play')}>
            <IconButton size="small" onClick={() => dispatch(setArmed({ monitorId, armed: !wall.armed }))}>
              {wall.armed ? <PauseIcon fontSize="small" /> : <PlayArrowIcon fontSize="small" />}
            </IconButton>
          </Tooltip>

          <Tooltip title={t('wall.fwd30')}>
            <IconButton size="small" onClick={() => handleNudge(NUDGE_MS)}>
              <Forward30Icon fontSize="small" />
            </IconButton>
          </Tooltip>

          <Tooltip title={t('wall.nextDay')}>
            <span>
              <IconButton size="small" disabled={!canStepFwd} onClick={() => handleDayStep(1)}>
                <ChevronRightIcon fontSize="small" />
              </IconButton>
            </span>
          </Tooltip>

          <PlayheadClock fallbackMs={wall.seekMs} />

          <Box sx={{ flex: 1 }} />

          {/* Scroll the visible window without moving the playhead. Half a window per click while
              there is still archive in view; past that it skips to the nearest recording rather
              than crawling across empty hours. Disabled when there is nothing that way. */}
          <Tooltip title={t('wall.panBack')}>
            <span>
              <IconButton size="small" disabled={panBackTarget === null} onClick={() => handlePan(-1)}>
                <KeyboardDoubleArrowLeftIcon fontSize="small" />
              </IconButton>
            </span>
          </Tooltip>

          <Tooltip title={t('wall.panForward')}>
            <span>
              <IconButton size="small" disabled={panFwdTarget === null} onClick={() => handlePan(1)}>
                <KeyboardDoubleArrowRightIcon fontSize="small" />
              </IconButton>
            </span>
          </Tooltip>

          <Tooltip title={t('wall.jumpTo')}>
            <IconButton size="small" onClick={(e) => setPickerAnchor(e.currentTarget)}>
              <EventIcon fontSize="small" />
            </IconButton>
          </Tooltip>

          <Tooltip title={t('wall.zoomOut')}>
            <IconButton size="small" onClick={() => dispatch(zoomWindow({ monitorId, factor: 1 / ZOOM_STEP }))}>
              <ZoomOutIcon fontSize="small" />
            </IconButton>
          </Tooltip>

          <Tooltip title={t('wall.zoomIn')}>
            <IconButton size="small" onClick={() => dispatch(zoomWindow({ monitorId, factor: ZOOM_STEP }))}>
              <ZoomInIcon fontSize="small" />
            </IconButton>
          </Tooltip>

          <Tooltip title={t('wall.zoomReset')}>
            <IconButton size="small" onClick={() => dispatch(resetZoom({ monitorId }))}>
              <ZoomOutMapIcon fontSize="small" />
            </IconButton>
          </Tooltip>

          <Popover
            open={Boolean(pickerAnchor)}
            anchorEl={pickerAnchor}
            onClose={() => setPickerAnchor(null)}
            anchorOrigin={{ vertical: 'top', horizontal: 'right' }}
            transformOrigin={{ vertical: 'bottom', horizontal: 'right' }}
          >
            <Box sx={{ p: 1.5 }}>
              {/* The pickers need their own localization context — the app has no global
                  LocalizationProvider, each picker site supplies one (same as RecordingsList). */}
              <LocalizationProvider dateAdapter={AdapterDayjs}>
                <DateTimePicker
                  ampm={false}
                  value={dayjs(wall.seekMs)}
                  onChange={(v) => {
                    if (!v?.isValid()) return
                    // Loads the target paused: jumping straight into playback at an arbitrary
                    // instant would run off the end of the archive before the operator can look
                    // at it.
                    wallClockBus.reset(v.valueOf())
                    dispatch(seekTo({ monitorId, seekMs: v.valueOf(), arm: false }))
                    setPickerAnchor(null)
                  }}
                />
              </LocalizationProvider>
            </Box>
          </Popover>
        </>
      )}

      {!playback && <Box sx={{ flex: 1 }} />}
    </Stack>
  )
}
