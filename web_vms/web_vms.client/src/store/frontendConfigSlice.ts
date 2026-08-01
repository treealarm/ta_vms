import { createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import { parseJson } from './api'

type FrontendConfigDto = {
  squareFrontendOrigin?: string | null
  liveTestPlayer?: boolean
}

type HealthDto = {
  ok: boolean
  dependencies: { name: string; ok: boolean; error?: string | null }[]
}

type FrontendConfigState = {
  squareFrontendOrigin: string | null
  /** Whether the backend serves the dev-only anonymous WHEP test endpoints at all. */
  liveTestPlayer: boolean
  /** This endpoint is anonymous, so 'failed' means the backend itself is unreachable, not 401. */
  status: 'idle' | 'pending' | 'succeeded' | 'failed'
  /** Last transport-level error while probing, shown once retries stop being a startup race. */
  lastError: string | null
  /** How many probes have failed in a row — the UI stays quiet for the first couple. */
  failedAttempts: number
  /** Dependencies the backend reports as broken (empty when everything is fine or unknown). */
  brokenDependencies: { name: string; error: string }[]
}

const initialState: FrontendConfigState = {
  squareFrontendOrigin: null,
  liveTestPlayer: false,
  status: 'idle',
  lastError: null,
  failedAttempts: 0,
  brokenDependencies: [],
}

export const loadFrontendConfig = createAsyncThunk('frontendConfig/load', async () => {
  const response = await fetch('/api/config/frontend')
  return parseJson<FrontendConfigDto>(response)
})

/** What the backend says about its own dependencies — anonymous, so it works before login. */
export const loadHealth = createAsyncThunk('frontendConfig/health', async () => {
  const response = await fetch('/api/health')
  return parseJson<HealthDto>(response)
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
        state.lastError = null
        state.failedAttempts = 0
        state.squareFrontendOrigin = action.payload.squareFrontendOrigin || null
        state.liveTestPlayer = action.payload.liveTestPlayer === true
      })
      .addCase(loadFrontendConfig.rejected, (state, action) => {
        state.status = 'failed'
        state.failedAttempts += 1
        state.lastError = action.error?.message ?? 'unknown error'
      })
      .addCase(loadHealth.fulfilled, (state, action) => {
        state.brokenDependencies = action.payload.dependencies
          .filter((d) => !d.ok)
          .map((d) => ({ name: d.name, error: d.error || 'unavailable' }))
      })
      .addCase(loadHealth.rejected, (state) => {
        // The health endpoint itself is unreachable — the config probe already covers that case.
        state.brokenDependencies = []
      })
  },
})

export default frontendConfigSlice.reducer
