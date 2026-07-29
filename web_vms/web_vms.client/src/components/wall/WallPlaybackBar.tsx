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
import VolumeUpIcon from '@mui/icons-material/VolumeUp'
import VolumeOffIcon from '@mui/icons-material/VolumeOff'
import KeyboardDoubleArrowLeftIcon from '@mui/icons-material/KeyboardDoubleArrowLeft'
import KeyboardDoubleArrowRightIcon from '@mui/icons-material/KeyboardDoubleArrowRight'
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
  setAudioEnabled,
  setWindow,
  zoomWindow,
  type WallMode,
} from '../../store/wallPlaybackSlice'
import { wallClockBus } from '../../hooks/wallClockBus'
import { adjacentCoveredDay, coveredDayKeys } from '../../utils/adjacentDay'
import { firstCoveredInRange, fitWindowToDay, panWindowToCoverage } from '../../utils/archiveRanges'
import { dayKeyLocal, dayKeyToLocalMidnightMs, localDayEndMs, MINUTE_MS } from '../../utils/wallTime'
import type { Segment } from '../../utils/archiveRanges'
import ArchiveDateTimePicker from '../ArchiveDateTimePicker'
import PlayheadClock from './PlayheadClock'

const NUDGE_MS = 30_000
// Floor for the window a day jump opens, so a day holding one short clip is not zoomed to seconds.
const MIN_JUMP_WINDOW_MS = 30 * MINUTE_MS
const ZOOM_STEP = 0.5

type Props = {
  monitorId: string
  /** Coverage of the focused camera — day navigation follows the camera the operator is watching. */
  focusedSegments: readonly Segment[]
  /** Camera the jump-to picker reports archive coverage for; null disables it. */
  focusedCameraId: string | null
}

export default function WallPlaybackBar({ monitorId, focusedSegments, focusedCameraId }: Props) {
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

  // Steps to the next day that actually holds archive, skipping empty ones however many there are.
  // Lands on that day's first recording rather than on its midnight, which is nearly always empty,
  // and frames the window on the day's actual material. Playback is left disarmed.
  const handleDayStep = (dir: -1 | 1) => {
    const key = adjacentCoveredDay(dayKeys, dayKeyLocal(wall.seekMs), dir)
    if (!key) return
    const dayStartMs = dayKeyToLocalMidnightMs(key)
    if (!Number.isFinite(dayStartMs)) return

    const seekMs = firstCoveredInRange(focusedSegments, dayStartMs, localDayEndMs(dayStartMs))
    const landing = seekMs ?? dayStartMs

    wallClockBus.reset(landing)
    dispatch(jumpToDay({ monitorId, dayStartMs, seekMs: landing }))

    const fit = fitWindowToDay(focusedSegments, landing, { minWidthMs: MIN_JUMP_WINDOW_MS })
    dispatch(setWindow({ monitorId, startMs: fit.startMs, widthMs: fit.widthMs }))
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

          {/* Sound follows the clock owner — the cell driving the playhead. Only one cell is ever
              audible; a wall of simultaneous audio is unusable. */}
          <Tooltip title={wall.audioEnabled ? t('wall.muteAudio') : t('wall.unmuteAudio')}>
            <IconButton
              size="small"
              onClick={() => dispatch(setAudioEnabled({ monitorId, enabled: !wall.audioEnabled }))}
            >
              {wall.audioEnabled ? <VolumeUpIcon fontSize="small" /> : <VolumeOffIcon fontSize="small" />}
            </IconButton>
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

          <Tooltip title={focusedCameraId ? t('wall.jumpTo') : t('wall.jumpToNeedsCamera')}>
            <span>
              <IconButton
                size="small"
                disabled={!focusedCameraId}
                onClick={(e) => setPickerAnchor(e.currentTarget)}
              >
                <EventIcon fontSize="small" />
              </IconButton>
            </span>
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
            {/* Same picker the per-cell recordings list uses: days and months without archive are
                greyed out and the hour strip shows where the day's material sits, so the operator
                cannot land on empty time. Fine positioning is then done on the timeline itself. */}
            {pickerAnchor && focusedCameraId && (
              <ArchiveDateTimePicker
                cameraId={focusedCameraId}
                value={dayjs(wall.seekMs)}
                onApply={(v) => {
                  if (!v?.isValid()) return
                  // Loads the target paused: jumping straight into playback at an arbitrary
                  // instant would run off the end of the archive before the operator can look at it.
                  wallClockBus.reset(v.valueOf())
                  dispatch(seekTo({ monitorId, seekMs: v.valueOf(), arm: false }))
                  setPickerAnchor(null)
                }}
                onCancel={() => setPickerAnchor(null)}
              />
            )}
          </Popover>
        </>
      )}

      {!playback && <Box sx={{ flex: 1 }} />}
    </Stack>
  )
}
