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

// The server answers newest-first with a LIMIT, so one page of a busy camera stops short of the
// window's start and the OLDEST events silently disappear from the timeline. Page backwards until
// the window is covered, bounded so a pathological camera cannot spin here forever.
const MAX_PAGES = 5

export const fetchWallEvents = createAsyncThunk('wallEvents/fetch', async (q: WallEventsQuery) => {
  const limit = q.limit ?? 200
  const merged: RetentionEventGroupsListDto = { groups: [], hasMore: false }
  let before = q.toUtc

  for (let page = 0; page < MAX_PAGES; page++) {
    // cameraId goes to the server rather than being filtered here: a global "newest N" page is
    // mostly other cameras' events, so the wanted ones would be crowded out before arrival.
    const params = new URLSearchParams({
      cameraId: q.cameraId,
      from: q.fromUtc,
      before,
      limit: String(limit),
    })
    const response = await fetch(`/api/fs/retention-events?${params.toString()}`)
    const dto = await parseJson<RetentionEventGroupsListDto>(response)

    merged.groups.push(...(dto.groups ?? []))
    if (!dto.hasMore || !dto.nextBeforeAlarmStart) break
    // Reached back past the window we care about.
    if (Date.parse(dto.nextBeforeAlarmStart) <= Date.parse(q.fromUtc)) break
    // No forward progress — stop rather than request the same page again.
    if (dto.nextBeforeAlarmStart === before) break
    before = dto.nextBeforeAlarmStart
  }

  return merged
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
