import { createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import { mapApi } from '../api/mapApi'
import type { MapMarker, MapPropertyFilter } from '../dto/mapDtos'

export type SearchState = {
  /** What the rows answer: a name or a set of properties; null before the first search. */
  mode: 'name' | 'props' | null
  rows: MapMarker[]
  loading: boolean
  error: string | null
}

const initialState: SearchState = {
  mode: null,
  rows: [],
  loading: false,
  error: null,
}

/** Nodes whose name contains the text; the map caps the answer at 20. */
export const searchByName = createAsyncThunk('search/byName', async (name: string) => {
  const response = await mapApi.getByName(name)
  return { mode: 'name' as const, rows: response.markers ?? [] }
})

/** Nodes carrying every given property with the given value, exactly. The answer is figures,
 *  so only nodes with a shape on a canvas can be found this way. */
export const searchByProps = createAsyncThunk(
  'search/byProps',
  async (props: MapPropertyFilter[]) => {
    const response = await mapApi.getByParams(props)
    return { mode: 'props' as const, rows: response.figs ?? [] }
  },
)

const searchSlice = createSlice({
  name: 'search',
  initialState,
  reducers: {
    clearSearch: (state) => {
      state.mode = null
      state.rows = []
      state.error = null
    },
  },
  extraReducers: (builder) => {
    builder
      .addCase(searchByName.pending, (state) => {
        state.loading = true
        state.error = null
      })
      .addCase(searchByProps.pending, (state) => {
        state.loading = true
        state.error = null
      })
      .addCase(searchByName.fulfilled, (state, action) => {
        state.loading = false
        state.mode = action.payload.mode
        state.rows = action.payload.rows
      })
      .addCase(searchByProps.fulfilled, (state, action) => {
        state.loading = false
        state.mode = action.payload.mode
        state.rows = action.payload.rows
      })
      .addMatcher(
        (action) => action.type.startsWith('search/') && action.type.endsWith('/rejected'),
        (state, action) => {
          state.loading = false
          state.error = (action as { error?: { message?: string } }).error?.message ?? 'Unknown error'
        },
      )
  },
})

export const { clearSearch } = searchSlice.actions
export default searchSlice.reducer
