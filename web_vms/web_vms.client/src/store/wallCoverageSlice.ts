import { createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import type { RootState } from '.'
import type { RecordingTimelineQueryDto, RecordingTimelineResponseDto } from '../dto/dtos'
import { parseJson } from './api'
import type { Segment } from '../utils/archiveRanges'

/**
 * Archive coverage for the wall timeline, keyed by the exact query that produced it.
 *
 * Kept apart from fsSlice on purpose: fsSlice caches clip-level data for the recordings list, while
 * this holds only interval boundaries for drawing. The two answer different questions at wildly
 * different sizes.
 */
type WallCoverageState = {
  byKey: Record<string, Record<string, Segment[]>>
  loadingKeys: string[]
  status: string
}

export const wallCoverageKey = (arg: RecordingTimelineQueryDto) =>
  `${[...arg.cameraIds].sort().join(',')}|${arg.streamName ?? ''}|${arg.fromUtc}|${arg.toUtc}|${arg.minGapMs}`

const initialState: WallCoverageState = { byKey: {}, loadingKeys: [], status: 'Ready' }

export const fetchRecordingTimeline = createAsyncThunk(
  'wallCoverage/fetch',
  async (payload: RecordingTimelineQueryDto) => {
    const response = await fetch('/api/fs/recording-timeline', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    })
    return parseJson<RecordingTimelineResponseDto>(response)
  }
)

const wallCoverageSlice = createSlice({
  name: 'wallCoverage',
  initialState,
  reducers: {},
  extraReducers: (builder) => {
    builder
      .addCase(fetchRecordingTimeline.pending, (state, action) => {
        const key = wallCoverageKey(action.meta.arg)
        if (!state.loadingKeys.includes(key)) state.loadingKeys.push(key)
      })
      .addCase(fetchRecordingTimeline.fulfilled, (state, action) => {
        const key = wallCoverageKey(action.meta.arg)
        state.loadingKeys = state.loadingKeys.filter((k) => k !== key)
        state.status = 'Ready'

        const byCamera: Record<string, Segment[]> = {}
        for (const track of action.payload.tracks ?? []) {
          byCamera[track.cameraId] = (track.intervals ?? [])
            .map((i) => ({ startMs: Date.parse(i.startTime), endMs: Date.parse(i.endTime) }))
            .filter((s) => Number.isFinite(s.startMs) && Number.isFinite(s.endMs) && s.endMs > s.startMs)
        }
        // Cameras the server knew nothing about must still resolve to "no coverage" rather than
        // "not loaded", or their cells would sit in a loading state forever.
        for (const cameraId of action.meta.arg.cameraIds) byCamera[cameraId] ??= []

        state.byKey[key] = byCamera
      })
      .addCase(fetchRecordingTimeline.rejected, (state, action) => {
        const key = wallCoverageKey(action.meta.arg)
        state.loadingKeys = state.loadingKeys.filter((k) => k !== key)
        state.status = `Error: ${action.error.message ?? 'recording timeline failed'}`
      })
  },
})

export const selectWallCoverage =
  (key: string) =>
  (state: RootState): Record<string, Segment[]> | undefined =>
    state.wallCoverage.byKey[key]

export const selectWallCoverageLoading =
  (key: string) =>
  (state: RootState): boolean =>
    state.wallCoverage.loadingKeys.includes(key)

export default wallCoverageSlice.reducer
