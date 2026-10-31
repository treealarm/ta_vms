import { createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import { mapApi } from '../api/mapApi'
import type { MapBox, MapTrackPoint } from '../dto/mapDtos'

export type TracksRequest = {
  /** The space whose rectangle is asked about; the answer is only shown in that space. */
  spaceId: string
  box: MapBox
  timeStart?: string
  timeEnd?: string
}

export type TracksState = {
  tracks: MapTrackPoint[]
  /** The space the shown tracks were read in; they are stale anywhere else. */
  spaceId: string | null
  loading: boolean
  error: string | null
}

const initialState: TracksState = {
  tracks: [],
  spaceId: null,
  loading: false,
  error: null,
}

/** The track points recorded in a rectangle over a period. The map refuses a box without both
 *  ends of the period, so the caller always passes them. */
export const loadTracks = createAsyncThunk(
  'tracks/loadTracks',
  async (request: TracksRequest) => {
    const response = await mapApi.getTracksByBox({
      box: request.box,
      timeStart: request.timeStart,
      timeEnd: request.timeEnd,
      // Newest first: a retro view opens on what just happened.
      sort: -1,
    })

    return { spaceId: request.spaceId, tracks: response.tracks ?? [] }
  },
)

const tracksSlice = createSlice({
  name: 'tracks',
  initialState,
  reducers: {
    clearTracks: (state) => {
      state.tracks = []
      state.spaceId = null
      state.error = null
    },
  },
  extraReducers: (builder) => {
    builder
      .addCase(loadTracks.pending, (state) => {
        state.loading = true
        state.error = null
      })
      .addCase(loadTracks.fulfilled, (state, action) => {
        state.loading = false
        state.tracks = action.payload.tracks
        state.spaceId = action.payload.spaceId
      })
      .addCase(loadTracks.rejected, (state, action) => {
        state.loading = false
        state.error = action.error.message ?? 'Unknown error'
      })
  },
})

export const { clearTracks } = tracksSlice.actions
export default tracksSlice.reducer
