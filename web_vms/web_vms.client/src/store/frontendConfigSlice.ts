import { createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import { parseJson } from './api'

type FrontendConfigDto = {
  squareFrontendOrigin?: string | null
  squareIntegrated?: boolean
  liveTestPlayer?: boolean
}

type HealthDto = {
  ok: boolean
  dependencies: { name: string; ok: boolean; starting?: boolean; error?: string | null }[]
}

type FrontendConfigState = {
  squareFrontendOrigin: string | null
  /**
   * This deployment really is integrated with Square: the integration is switched on and has
   * reached Square at least once. Separate from knowing where Square's UI lives — that value also
   * gates CORS and is set in dev whether or not a Square is running.
   */
  squareIntegrated: boolean
  /** Whether the backend serves the dev-only anonymous WHEP test endpoints at all. */
  liveTestPlayer: boolean
  /** This endpoint is anonymous, so 'failed' means the backend itself is unreachable, not 401. */
  status: 'idle' | 'pending' | 'succeeded' | 'failed'
  /** Last transport-level error while probing, shown once retries stop being a startup race. */
  lastError: string | null
  /** How many probes have failed in a row — the UI stays quiet for the first couple. */
  failedAttempts: number
  /** Dependencies the backend reports as broken (empty when everything is fine or unknown). */
  /**
   * Dependencies the gateway currently reports as down, with how many consecutive health polls have
   * said so. The count is what lets the UI ignore the normal startup window, when services are
   * still coming up and every dependency is briefly "unavailable".
   */
  brokenDependencies: { name: string; error: string; consecutive: number }[]
}

const initialState: FrontendConfigState = {
  squareFrontendOrigin: null,
  squareIntegrated: false,
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
        state.squareIntegrated = action.payload.squareIntegrated === true
        state.liveTestPlayer = action.payload.liveTestPlayer === true
      })
      .addCase(loadFrontendConfig.rejected, (state, action) => {
        state.status = 'failed'
        state.failedAttempts += 1
        state.lastError = action.error?.message ?? 'unknown error'
      })
      .addCase(loadHealth.fulfilled, (state, action) => {
        // Counting per name, not per poll: a dependency that recovers and breaks again starts over,
        // and one that just broke is not masked by another that has been down for a while.
        const previous = new Map(state.brokenDependencies.map((d) => [d.name, d.consecutive]))
        state.brokenDependencies = action.payload.dependencies
          // `starting` is the gateway saying it has never reached this dependency and is still
          // young enough not to have expected to. Launching everything at once puts every poll of
          // the first half-minute in that state, and a banner there is noise, not news.
          .filter((d) => !d.ok && !d.starting)
          .map((d) => ({
            name: d.name,
            error: d.error || 'unavailable',
            consecutive: (previous.get(d.name) ?? 0) + 1,
          }))
      })
      .addCase(loadHealth.rejected, (state) => {
        // The health endpoint itself is unreachable — the config probe already covers that case.
        state.brokenDependencies = []
      })
  },
})

export default frontendConfigSlice.reducer
