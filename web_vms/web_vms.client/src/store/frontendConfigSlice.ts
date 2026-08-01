import { createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import { parseJson } from './api'

type FrontendConfigDto = {
  squareFrontendOrigin?: string | null
  liveTestPlayer?: boolean
}

type FrontendConfigState = {
  squareFrontendOrigin: string | null
  /** Whether the backend serves the dev-only anonymous WHEP test endpoints at all. */
  liveTestPlayer: boolean
  /** This endpoint is anonymous, so 'failed' means the backend itself is unreachable, not 401. */
  status: 'idle' | 'pending' | 'succeeded' | 'failed'
}

const initialState: FrontendConfigState = {
  squareFrontendOrigin: null,
  liveTestPlayer: false,
  status: 'idle',
}

export const loadFrontendConfig = createAsyncThunk('frontendConfig/load', async () => {
  const response = await fetch('/api/config/frontend')
  return parseJson<FrontendConfigDto>(response)
})

const frontendConfigSlice = createSlice({
  name: 'frontendConfig',
  initialState,
  reducers: {},
  extraReducers: (builder) => {
    builder
      .addCase(loadFrontendConfig.pending, (state) => {
        state.status = 'pending'
      })
      .addCase(loadFrontendConfig.fulfilled, (state, action) => {
        state.status = 'succeeded'
        state.squareFrontendOrigin = action.payload.squareFrontendOrigin || null
        state.liveTestPlayer = action.payload.liveTestPlayer === true
      })
      .addCase(loadFrontendConfig.rejected, (state) => {
        state.status = 'failed'
      })
  },
})

export default frontendConfigSlice.reducer
