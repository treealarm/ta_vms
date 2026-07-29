import { createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import type { RootState } from '.'
import type { EpisodeDto, RetentionEventGroupsListDto } from '../dto/dtos'
import { parseJson } from './api'

/** One retention episode reduced to what the timeline draws. */
export type WallEvent = {
  eventUuid: string
  eventId: string
  cameraId: string
  startMs: number
  /** Null while the episode is still open. */
  endMs: number | null
  objectId?: string
}

type WallEventsState = {
  byKey: Record<string, WallEvent[]>
  loadingKeys: string[]
  status: string
}

export type WallEventsQuery = {
  cameraId: string
  fromUtc: string
  toUtc: string
  limit?: number
}

export const wallEventsKey = (q: WallEventsQuery) => `${q.cameraId}|${q.fromUtc}|${q.toUtc}`

const initialState: WallEventsState = { byKey: {}, loadingKeys: [], status: 'Ready' }

export const fetchWallEvents = createAsyncThunk('wallEvents/fetch', async (q: WallEventsQuery) => {
  // cameraId is passed to the server rather than filtered here: a global "newest N" page is mostly
  // other cameras' events, so the ones being looked for would be crowded out before arrival.
  const params = new URLSearchParams({
    cameraId: q.cameraId,
    from: q.fromUtc,
    before: q.toUtc,
    limit: String(q.limit ?? 200),
  })
  const response = await fetch(`/api/fs/retention-events?${params.toString()}`)
  return parseJson<RetentionEventGroupsListDto>(response)
})

function toWallEvent(ep: EpisodeDto): WallEvent | null {
  const startMs = Date.parse(ep.alarmStartTs)
  if (!Number.isFinite(startMs)) return null
  const endParsed = ep.alarmEndTs ? Date.parse(ep.alarmEndTs) : NaN
  return {
    eventUuid: ep.eventUuid,
    eventId: ep.eventId,
    cameraId: ep.cameraId,
    startMs,
    endMs: Number.isFinite(endParsed) && endParsed > startMs ? endParsed : null,
    objectId: ep.objectId,
  }
}

const wallEventsSlice = createSlice({
  name: 'wallEvents',
  initialState,
  reducers: {},
  extraReducers: (builder) => {
    builder
      .addCase(fetchWallEvents.pending, (state, action) => {
        const key = wallEventsKey(action.meta.arg)
        if (!state.loadingKeys.includes(key)) state.loadingKeys.push(key)
      })
      .addCase(fetchWallEvents.fulfilled, (state, action) => {
        const key = wallEventsKey(action.meta.arg)
        state.loadingKeys = state.loadingKeys.filter((k) => k !== key)
        state.status = 'Ready'

        const seen = new Set<string>()
        const events: WallEvent[] = []
        for (const group of action.payload.groups ?? []) {
          for (const ep of group.episodes ?? []) {
            if (ep.cameraId !== action.meta.arg.cameraId) continue
            if (seen.has(ep.eventUuid)) continue
            const ev = toWallEvent(ep)
            if (!ev) continue
            seen.add(ep.eventUuid)
            events.push(ev)
          }
        }
        events.sort((a, b) => a.startMs - b.startMs)
        state.byKey[key] = events
      })
      .addCase(fetchWallEvents.rejected, (state, action) => {
        const key = wallEventsKey(action.meta.arg)
        state.loadingKeys = state.loadingKeys.filter((k) => k !== key)
        state.status = `Error: ${action.error.message ?? 'wall events failed'}`
      })
  },
})

export const selectWallEvents =
  (key: string) =>
  (state: RootState): WallEvent[] | undefined =>
    state.wallEvents.byKey[key]

export default wallEventsSlice.reducer
