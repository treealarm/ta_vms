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
  /** Queries that failed outright — the timeline draws "archive unavailable", not "no recording". */
  failedKeys: string[]
  /** Cameras a successful response carried no track for: the camera, not the archive, was silent. */
  missingByKey: Record<string, string[]>
  status: string
}

export const wallCoverageKey = (arg: RecordingTimelineQueryDto) =>
  `${[...arg.cameraIds].sort().join(',')}|${arg.streamName ?? ''}|${arg.fromUtc}|${arg.toUtc}|${arg.minGapMs}`

const initialState: WallCoverageState = {
  byKey: {},
  loadingKeys: [],
  failedKeys: [],
  missingByKey: {},
  status: 'Ready',
}

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
        state.failedKeys = state.failedKeys.filter((k) => k !== key)
        state.status = 'Ready'

        const byCamera: Record<string, Segment[]> = {}
        for (const track of action.payload.tracks ?? []) {
          byCamera[track.cameraId] = (track.intervals ?? [])
            .map((i) => ({
              startMs: Date.parse(i.startTime),
              endMs: Date.parse(i.endTime),
              origin: i.origin,
              roiState: i.roiState,
            }))
            .filter((s) => Number.isFinite(s.startMs) && Number.isFinite(s.endMs) && s.endMs > s.startMs)
        }
        // A camera the server returns no track for did not answer; one it answers with an empty
        // interval list simply has no recording. The timeline shows the first as unknown, so the
        // distinction is kept before the missing entries are filled in for the cells.
        state.missingByKey[key] = action.meta.arg.cameraIds.filter((id) => !(id in byCamera))
        // Every requested camera must still resolve to "no coverage" rather than "not loaded", or
        // its cell would sit in a loading state forever.
        for (const cameraId of action.meta.arg.cameraIds) byCamera[cameraId] ??= []

        state.byKey[key] = byCamera
      })
      .addCase(fetchRecordingTimeline.rejected, (state, action) => {
        const key = wallCoverageKey(action.meta.arg)
        state.loadingKeys = state.loadingKeys.filter((k) => k !== key)
        if (!state.failedKeys.includes(key)) state.failedKeys.push(key)
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

export const selectWallCoverageFailed =
  (key: string) =>
  (state: RootState): boolean =>
    state.wallCoverage.failedKeys.includes(key)

export const selectWallCoverageMissing =
  (key: string) =>
  (state: RootState): string[] | undefined =>
    state.wallCoverage.missingByKey[key]

export default wallCoverageSlice.reducer
