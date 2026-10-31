import { createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import { mapApi } from '../api/mapApi'
import type { MapAlarmState, MapObjectState, MapStateDescription } from '../dto/mapDtos'
import { loadLatestAlarmEvent } from './eventsSlice'

export type StatesState = {
  states: Record<string, MapObjectState>
  alarms: Record<string, MapAlarmState>
  /** The state dictionary, keyed by state name; the map paints from it. */
  descriptions: Record<string, MapStateDescription>
  /** Every alarmed node the caller may see, for the operator's alarm list. */
  all: MapAlarmState[]
  descriptionsLoaded: boolean
  error: string | null
}

const initialState: StatesState = {
  states: {},
  alarms: {},
  descriptions: {},
  all: [],
  descriptionsLoaded: false,
  error: null,
}

/**
 * States and alarm counters of a set of nodes, plus the descriptions of the states they carry.
 * A node whose alarm just cleared is asked for its newest alarm event: the state is NORM now,
 * but the pale ring the operator expects still has to know when the episode ended.
 */
export const loadVisualStates = createAsyncThunk(
  'states/loadVisualStates',
  async (ids: string[], api) => {
    const response = await mapApi.getVisualStates(ids)
    const previous = (api.getState() as { states: StatesState }).states.alarms

    const cleared = (response.alarmedObjects ?? []).filter((alarm) =>
      alarm.id &&
      !alarm.alarm &&
      !(alarm.childrenAlarms ?? 0) &&
      previous[alarm.id]?.alarm)

    for (const alarm of cleared) void api.dispatch(loadLatestAlarmEvent(alarm.id!))

    return response
  },
)

export const loadStateDescriptions = createAsyncThunk(
  'states/loadStateDescriptions',
  async () => mapApi.getStateDescriptions([]),
)

export const loadAlarmedObjects = createAsyncThunk(
  'states/loadAlarmedObjects',
  async () => mapApi.getAlarmedStates([]),
)

const statesSlice = createSlice({
  name: 'states',
  initialState,
  reducers: {
    clearStatesError: (state) => {
      state.error = null
    },
  },
  extraReducers: (builder) => {
    builder
      .addCase(loadVisualStates.fulfilled, (state, action) => {
        for (const value of action.payload.states ?? []) {
          if (value.id) state.states[value.id] = value
        }

        for (const alarm of action.payload.alarmedObjects ?? []) {
          if (alarm.id) state.alarms[alarm.id] = alarm
        }

        for (const description of action.payload.statesDescr ?? []) {
          if (description.state) state.descriptions[description.state] = description
        }

        state.error = null
      })
      .addCase(loadStateDescriptions.fulfilled, (state, action) => {
        for (const description of action.payload.descriptions ?? []) {
          if (description.state) state.descriptions[description.state] = description
        }

        state.descriptionsLoaded = true
        state.error = null
      })
      .addCase(loadAlarmedObjects.fulfilled, (state, action) => {
        // The full answer replaces the list: a node that stopped alarming has to leave it.
        state.all = action.payload.states ?? []
        state.error = null
      })
      .addMatcher(
        (action) => action.type.startsWith('states/') && action.type.endsWith('/rejected'),
        (state, action) => {
          state.error = (action as { error?: { message?: string } }).error?.message ?? 'Unknown error'
        },
      )
  },
})

export const { clearStatesError } = statesSlice.actions
export default statesSlice.reducer
