import { createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import { mapApi } from '../api/mapApi'
import type { MapEvent } from '../dto/mapDtos'

/** How far back an alarm that has ended still shows, in minutes. */
export const RECENT_ALARM_MINUTES = 15
export const DEFAULT_EVENT_COUNT = 100

export type EventsFilter = {
  timeStart?: string
  timeEnd?: string
  /** The named nodes and everything below them; empty is the whole journal. */
  objects: string[]
  imagesOnly: boolean
  param0: string
  param1: string
  count: number
}

export const emptyEventsFilter: EventsFilter = {
  objects: [],
  imagesOnly: false,
  param0: '',
  param1: '',
  count: DEFAULT_EVENT_COUNT,
}

export type EventsState = {
  filter: EventsFilter
  rows: MapEvent[]
  hasMore: boolean
  loading: boolean
  error: string | null
  /** Alarm events kept for the map rings and the alarm list: running and recently ended. */
  recentAlarms: MapEvent[]
  /** The last events of one node, for the object panel; the journal keeps its own paged rows. */
  byObject: Record<string, MapEvent[]>
}

const initialState: EventsState = {
  filter: emptyEventsFilter,
  rows: [],
  hasMore: false,
  loading: false,
  error: null,
  recentAlarms: [],
  byObject: {},
}

const wireFilter = (filter: EventsFilter, startId?: string) => ({
  timeStart: filter.timeStart,
  timeEnd: filter.timeEnd,
  objects: filter.objects,
  imagesOnly: filter.imagesOnly,
  param0: filter.param0 || undefined,
  param1: filter.param1 || undefined,
  startId,
  count: filter.count,
  sortKey: 'timestamp',
  // The journal opens on the newest events; paging walks backwards by keyset.
  sortOrder: 'desc',
})

export const loadEvents = createAsyncThunk(
  'events/loadEvents',
  async ({ filter, append }: { filter: EventsFilter; append?: boolean }, api) => {
    const rows = (api.getState() as { events: EventsState }).events.rows
    const startId = append ? rows[rows.length - 1]?.id : undefined
    const response = await mapApi.getEvents(wireFilter(filter, startId))
    return { filter, events: response.events ?? [], append: !!append }
  },
)

/** The last `windowMinutes` of alarm events, for the rings of alarms that ended while the page
 *  was not watching. Running alarms come from the states; this fills in what states forget. */
export const loadRecentAlarms = createAsyncThunk(
  'events/loadRecentAlarms',
  async (windowMinutes: number = RECENT_ALARM_MINUTES) => {
    const timeStart = new Date(Date.now() - windowMinutes * 60_000).toISOString()
    const response = await mapApi.getEvents({
      timeStart,
      count: 500,
      sortKey: 'timestamp',
      sortOrder: 'desc',
    })

    return (response.events ?? []).filter((event) => event.eventName === 'alarm')
  },
)

/** The last events of one node, shown in the object panel. */
export const loadObjectEvents = createAsyncThunk(
  'events/loadObjectEvents',
  async (objectId: string) => {
    const response = await mapApi.getEvents({
      objects: [objectId],
      count: 15,
      sortKey: 'timestamp',
      sortOrder: 'desc',
    })

    return { objectId, events: response.events ?? [] }
  },
)

/** One node's newest alarm event, asked when its alarm clears: the state is gone, the ring is
 *  not, and the event carries the end it ended at. */
export const loadLatestAlarmEvent = createAsyncThunk(
  'events/loadLatestAlarmEvent',
  async (objectId: string) => {
    const response = await mapApi.getEvents({
      objects: [objectId],
      count: 5,
      sortKey: 'timestamp',
      sortOrder: 'desc',
    })

    return (response.events ?? []).find((event) => event.eventName === 'alarm') ?? null
  },
)

export function eventProp(event: MapEvent, name: string): string | undefined {
  return (event.extraProps ?? []).find((prop) => prop.propName === name)?.strVal || undefined
}

/** When the alarm ends, in epoch milliseconds; absent means it is still running. */
export function alarmEndMs(event: MapEvent): number | undefined {
  const text = eventProp(event, 'alarm_end_ts')
  if (!text) return undefined

  const value = Date.parse(text)
  return Number.isNaN(value) ? undefined : value
}

export function isRunningAlarm(event: MapEvent, now = Date.now()): boolean {
  const end = alarmEndMs(event)
  return end === undefined || end > now
}

/** Keeps one alarm event per object: the newest. */
function mergeAlarm(events: MapEvent[], event: MapEvent): MapEvent[] {
  const others = events.filter((known) => known.objectId !== event.objectId)
  return [...others, event]
}

const eventsSlice = createSlice({
  name: 'events',
  initialState,
  reducers: {
    clearEventsError: (state) => {
      state.error = null
    },
  },
  extraReducers: (builder) => {
    builder
      .addCase(loadEvents.pending, (state) => {
        state.loading = true
      })
      .addCase(loadEvents.fulfilled, (state, action) => {
        state.loading = false
        state.error = null
        state.filter = action.payload.filter
        state.rows = action.payload.append
          ? [...state.rows, ...action.payload.events]
          : action.payload.events
        state.hasMore = action.payload.events.length >= action.payload.filter.count
      })
      .addCase(loadEvents.rejected, (state, action) => {
        state.loading = false
        state.error = action.error.message ?? 'Unknown error'
      })
      .addCase(loadRecentAlarms.fulfilled, (state, action) => {
        // The fetched window replaces the remembered list for the same objects; a locally
        // remembered event that is still recent survives, because its start may be older than
        // the window (an alarm that ran for an hour and ended a minute ago).
        const cutoff = Date.now() - RECENT_ALARM_MINUTES * 60_000
        const kept = state.recentAlarms.filter((event) => {
          const end = alarmEndMs(event)
          return end === undefined || end >= cutoff
        })

        let merged = kept
        for (const event of action.payload) merged = mergeAlarm(merged, event)
        state.recentAlarms = merged
      })
      .addCase(loadLatestAlarmEvent.fulfilled, (state, action) => {
        if (action.payload) state.recentAlarms = mergeAlarm(state.recentAlarms, action.payload)
      })
      .addCase(loadObjectEvents.fulfilled, (state, action) => {
        state.byObject[action.payload.objectId] = action.payload.events
      })
  },
})

export const { clearEventsError } = eventsSlice.actions
export default eventsSlice.reducer
