import { createSlice, type PayloadAction } from '@reduxjs/toolkit'
import type { RootState } from '.'
import { addLocalDays, DAY_MS, HOUR_MS, clampMs, localDayStartMs, MINUTE_MS } from '../utils/wallTime'

export type WallMode = 'live' | 'playback'

export type WallState = {
  mode: WallMode
  /**
   * Last COMMITTED position — the anchor every cell builds its playlist from. It changes only on a
   * deliberate action (click, nudge, day jump, event), never on playback progress: rebuilding the
   * HLS source on every timeupdate would restart the players several times a second.
   */
  seekMs: number
  /** Whether cells are allowed to play. Day jumps deliberately leave this false. */
  armed: boolean
  /** Bumped whenever play is (re-)authorized, so cells call play() again even if `armed` was already true. */
  playEpoch: number
  windowStartMs: number
  windowWidthMs: number
  focusedSlotIndex: number | null
  /**
   * Set once on entering playback: if the focused camera has nothing recorded today, the wall jumps
   * to the last available archive. Cleared after the first positioning or any manual seek, so
   * switching focus later never moves the wall's day on its own.
   */
  initialJumpPending: boolean
}

const DEFAULT_WINDOW_MS = 2 * HOUR_MS
const MIN_WINDOW_MS = 2 * MINUTE_MS
const MAX_WINDOW_MS = 14 * DAY_MS

function makeWallState(nowMs: number): WallState {
  return {
    mode: 'live',
    seekMs: nowMs,
    armed: false,
    playEpoch: 0,
    windowStartMs: nowMs - DEFAULT_WINDOW_MS / 2,
    windowWidthMs: DEFAULT_WINDOW_MS,
    focusedSlotIndex: null,
    initialJumpPending: false,
  }
}

type State = { byMonitor: Record<string, WallState> }

const initialState: State = { byMonitor: {} }

function ensure(state: State, monitorId: string): WallState {
  let w = state.byMonitor[monitorId]
  if (!w) {
    w = makeWallState(Date.now())
    state.byMonitor[monitorId] = w
  }
  return w
}

/**
 * Opens the whole local day containing `ms`. This is the default view for playback: recording
 * comes in bursts scattered across the day, and a few-hour window shows only the latest burst,
 * which reads as "there is no archive" even when there is plenty earlier the same day.
 */
function showLocalDay(w: WallState, ms: number) {
  const start = localDayStartMs(ms)
  w.windowStartMs = start
  w.windowWidthMs = addLocalDays(start, 1) - start
}

type Id = { monitorId: string }

const wallPlaybackSlice = createSlice({
  name: 'wallPlayback',
  initialState,
  reducers: {
    enterPlayback(state, { payload }: PayloadAction<Id & { seekMs?: number }>) {
      const w = ensure(state, payload.monitorId)
      w.mode = 'playback'
      w.seekMs = payload.seekMs ?? Date.now()
      // Parked, not playing: entering playback at "now" and immediately starting would run off the
      // end of the archive within seconds. The operator arms playback by clicking the timeline.
      w.armed = false
      w.initialJumpPending = true
      showLocalDay(w, w.seekMs)
    },

    exitPlayback(state, { payload }: PayloadAction<Id>) {
      const w = ensure(state, payload.monitorId)
      w.mode = 'live'
      w.armed = false
      w.initialJumpPending = false
    },

    seekTo(state, { payload }: PayloadAction<Id & { seekMs: number; arm?: boolean }>) {
      const w = ensure(state, payload.monitorId)
      if (!Number.isFinite(payload.seekMs)) return
      w.seekMs = payload.seekMs
      if (payload.arm !== undefined) w.armed = payload.arm
      if (w.armed) w.playEpoch += 1
      w.initialJumpPending = false
    },

    nudgeSeek(state, { payload }: PayloadAction<Id & { deltaMs: number }>) {
      const w = ensure(state, payload.monitorId)
      w.seekMs += payload.deltaMs
      w.initialJumpPending = false
      if (w.armed) w.playEpoch += 1
    },

    setArmed(state, { payload }: PayloadAction<Id & { armed: boolean }>) {
      const w = ensure(state, payload.monitorId)
      w.armed = payload.armed
      if (payload.armed) w.playEpoch += 1
    },

    bumpPlayEpoch(state, { payload }: PayloadAction<Id>) {
      ensure(state, payload.monitorId).playEpoch += 1
    },

    setWindow(state, { payload }: PayloadAction<Id & { startMs: number; widthMs: number }>) {
      const w = ensure(state, payload.monitorId)
      w.windowWidthMs = clampMs(payload.widthMs, MIN_WINDOW_MS, MAX_WINDOW_MS)
      w.windowStartMs = payload.startMs
    },

    panWindow(state, { payload }: PayloadAction<Id & { deltaMs: number }>) {
      ensure(state, payload.monitorId).windowStartMs += payload.deltaMs
    },

    /** `factor` < 1 zooms in. `anchorMs` stays put on screen. */
    zoomWindow(state, { payload }: PayloadAction<Id & { factor: number; anchorMs?: number }>) {
      const w = ensure(state, payload.monitorId)
      const anchor = payload.anchorMs ?? w.windowStartMs + w.windowWidthMs / 2
      const ratio = (anchor - w.windowStartMs) / w.windowWidthMs
      const next = clampMs(w.windowWidthMs * payload.factor, MIN_WINDOW_MS, MAX_WINDOW_MS)
      w.windowStartMs = anchor - ratio * next
      w.windowWidthMs = next
    },

    resetZoom(state, { payload }: PayloadAction<Id>) {
      const w = ensure(state, payload.monitorId)
      showLocalDay(w, w.seekMs)
    },

    /**
     * Land on a specific local midnight. Deliberately does NOT arm playback and does NOT snap the
     * target to already-loaded coverage: a snap window wide enough to be useful mid-day would pull
     * a midnight target back into the previous day, undoing the jump.
     */
    jumpToDay(state, { payload }: PayloadAction<Id & { dayStartMs: number }>) {
      const w = ensure(state, payload.monitorId)
      const start = localDayStartMs(payload.dayStartMs)
      w.seekMs = start
      w.armed = false
      w.initialJumpPending = false
      showLocalDay(w, start)
    },

    setFocusedSlot(state, { payload }: PayloadAction<Id & { slotIndex: number | null }>) {
      ensure(state, payload.monitorId).focusedSlotIndex = payload.slotIndex
    },

    clearInitialJump(state, { payload }: PayloadAction<Id>) {
      ensure(state, payload.monitorId).initialJumpPending = false
    },
  },
})

export const {
  enterPlayback,
  exitPlayback,
  seekTo,
  nudgeSeek,
  setArmed,
  bumpPlayEpoch,
  setWindow,
  panWindow,
  zoomWindow,
  resetZoom,
  jumpToDay,
  setFocusedSlot,
  clearInitialJump,
} = wallPlaybackSlice.actions

const FALLBACK = makeWallState(0)

export const selectWall = (monitorId: string) => (state: RootState): WallState =>
  state.wallPlayback.byMonitor[monitorId] ?? FALLBACK

export const selectWallMode = (monitorId: string) => (state: RootState): WallMode =>
  state.wallPlayback.byMonitor[monitorId]?.mode ?? 'live'

export const selectWallSeekMs = (monitorId: string) => (state: RootState): number =>
  state.wallPlayback.byMonitor[monitorId]?.seekMs ?? 0

export const selectWallArmed = (monitorId: string) => (state: RootState): boolean =>
  state.wallPlayback.byMonitor[monitorId]?.armed ?? false

export const selectWallPlayEpoch = (monitorId: string) => (state: RootState): number =>
  state.wallPlayback.byMonitor[monitorId]?.playEpoch ?? 0

export const selectFocusedSlot = (monitorId: string) => (state: RootState): number | null =>
  state.wallPlayback.byMonitor[monitorId]?.focusedSlotIndex ?? null

export { DEFAULT_WINDOW_MS, MIN_WINDOW_MS, MAX_WINDOW_MS }

export default wallPlaybackSlice.reducer
