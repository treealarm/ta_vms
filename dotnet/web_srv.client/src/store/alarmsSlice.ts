import { createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import { mapApi } from '../api/mapApi'
import type { MapAlarm, MapAlarmCursor } from '../dto/mapDtos'

/** Rows per branch; the list pages by keyset, so this is a page, not a cap. */
export const ALARM_PAGE_SIZE = 100

export type AlarmsState = {
  open: MapAlarm[]
  closed: MapAlarm[]
  openCursor?: MapAlarmCursor
  closedCursor?: MapAlarmCursor
  openHasMore: boolean
  closedHasMore: boolean
  loading: boolean
  error: string | null
}

const initialState: AlarmsState = {
  open: [],
  closed: [],
  openHasMore: false,
  closedHasMore: false,
  loading: false,
  error: null,
}

/**
 * The two pages in one call. `more` continues both branches from the cursors the last answer
 * gave; a fresh load starts at the newest row of each.
 */
export const loadAlarms = createAsyncThunk(
  'alarms/load',
  async ({ more = false }: { more?: boolean } = {}, api) => {
    const state = (api.getState() as { alarms: AlarmsState }).alarms
    const response = await mapApi.getAlarms({
      pageSize: ALARM_PAGE_SIZE,
      openAfter: more ? state.openCursor : undefined,
      closedAfter: more ? state.closedCursor : undefined,
    })

    return {
      more,
      open: response.open?.alarms ?? [],
      closed: response.closed?.alarms ?? [],
      openHasMore: !!response.open?.hasMore,
      closedHasMore: !!response.closed?.hasMore,
      openCursor: response.open?.next,
      closedCursor: response.closed?.next,
    }
  },
)

export const acknowledgeAlarm = createAsyncThunk(
  'alarms/acknowledge',
  async ({ id, comment }: { id: string; comment: string }) =>
    mapApi.acknowledgeAlarm(id, comment),
)

export const closeAlarm = createAsyncThunk(
  'alarms/close',
  async ({ id, reason, comment }: { id: string; reason: 'false_alarm' | 'other'; comment: string }) =>
    mapApi.closeAlarm(id, reason, comment),
)

/** One row changed: take it out of both lists and put it where its status now belongs. */
function place(state: AlarmsState, alarm: MapAlarm) {
  if (!alarm.id) return

  state.open = state.open.filter((known) => known.id !== alarm.id)
  state.closed = state.closed.filter((known) => known.id !== alarm.id)

  if (alarm.status === 'closed') state.closed = [alarm, ...state.closed]
  else state.open = [alarm, ...state.open]
}

const alarmsSlice = createSlice({
  name: 'alarms',
  initialState,
  reducers: {
    clearAlarmsError: (state) => {
      state.error = null
    },
  },
  extraReducers: (builder) => {
    builder
      .addCase(loadAlarms.pending, (state) => {
        state.loading = true
      })
      .addCase(loadAlarms.fulfilled, (state, action) => {
        state.loading = false
        state.error = null

        const page = action.payload
        state.open = page.more ? [...state.open, ...page.open] : page.open
        state.closed = page.more ? [...state.closed, ...page.closed] : page.closed
        state.openHasMore = page.openHasMore
        state.closedHasMore = page.closedHasMore
        state.openCursor = page.openCursor
        state.closedCursor = page.closedCursor
      })
      .addCase(loadAlarms.rejected, (state, action) => {
        state.loading = false
        state.error = action.error.message ?? 'Unknown error'
      })
      .addCase(acknowledgeAlarm.fulfilled, (state, action) => {
        place(state, action.payload)
      })
      .addCase(closeAlarm.fulfilled, (state, action) => {
        place(state, action.payload)
      })
  },
})

export const { clearAlarmsError } = alarmsSlice.actions
export default alarmsSlice.reducer
