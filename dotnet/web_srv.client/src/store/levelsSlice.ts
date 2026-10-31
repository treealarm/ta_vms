import { createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import { mapApi } from '../api/mapApi'
import type { MapLevel } from '../dto/mapDtos'

export type LevelsState = {
  levels: MapLevel[]
  loaded: boolean
  error: string | null
}

const initialState: LevelsState = { levels: [], loaded: false, error: null }

/** The zoom-level dictionary: a figure with a level is drawn only while the map is at that zoom. */
export const loadLevels = createAsyncThunk('levels/loadLevels', async () => {
  const response = await mapApi.getLevels()
  return response.levels ?? []
})

const levelsSlice = createSlice({
  name: 'levels',
  initialState,
  reducers: {},
  extraReducers: (builder) => {
    builder
      .addCase(loadLevels.fulfilled, (state, action) => {
        state.levels = action.payload
        state.loaded = true
        state.error = null
      })
      .addCase(loadLevels.rejected, (state, action) => {
        state.error = action.error.message ?? 'Unknown error'
      })
  },
})

export default levelsSlice.reducer
