import { type AnyAction, createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import type { LogEntryDto, LogQueryDto, LogResponseDto } from '../dto/dtos'
import { parseJson } from './api'

type LogState = {
  entries: LogEntryDto[]
  hasMore: boolean
  isBusy: boolean
  status: string
  lastQuery: LogQueryDto | null
  panelOpen: boolean
}

const initialState: LogState = {
  entries: [],
  hasMore: false,
  isBusy: false,
  status: 'Ready',
  lastQuery: null,
  panelOpen: false
}

export const fetchLogs = createAsyncThunk(
  'logs/fetchLogs',
  async (payload: LogQueryDto & { merge?: boolean }) => {
    const query: LogQueryDto = {
      from: payload.from,
      to: payload.to,
      limit: payload.limit,
      level: payload.level,
      source: payload.source,
      appId: payload.appId,
    }
    const response = await fetch('/api/logger/logs', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(query)
    })

    return parseJson<LogResponseDto>(response)
  }
)

const logSlice = createSlice({
  name: 'logs',
  initialState,
  reducers: {
    clearLogs(state) {
      state.entries = []
      state.hasMore = false
      state.status = 'Ready'
    },
    toggleLogPanel(state) {
      state.panelOpen = !state.panelOpen
    }
  },
  extraReducers: (builder) => {
    builder
      .addCase(fetchLogs.fulfilled, (state, action: AnyAction) => {
        const { logs, hasMore } = action.payload as LogResponseDto
        if (action.meta.arg.merge && state.entries.length > 0) {
          // Incremental poll: append entries newer than what we already have, deduping
          // by timestamp+message (the backend's `from` is inclusive, and the client-side
          // `from` is rounded to millisecond precision, so the newest known entry can
          // come back again).
          const seen = new Set(state.entries.map((e) => `${e.timestamp}|${e.message}`))
          const newOnes = logs.filter((e) => !seen.has(`${e.timestamp}|${e.message}`))
          state.entries = [...state.entries, ...newOnes]

          // Cap memory growth over long-running sessions.
          const cap = Math.max(action.meta.arg.limit ?? 200, 1000)
          if (state.entries.length > cap) {
            state.entries = state.entries.slice(state.entries.length - cap)
          }
          state.status = `Loaded ${state.entries.length} logs`
        } else {
          state.entries = logs
          state.status = `Loaded ${logs.length} logs`
        }
        state.hasMore = hasMore
        state.lastQuery = action.meta.arg
      })
      .addMatcher((action: AnyAction) => action.type.startsWith('logs/') && action.type.endsWith('/pending'), (state) => {
        state.isBusy = true
      })
      .addMatcher((action: AnyAction) => action.type.startsWith('logs/') && action.type.endsWith('/fulfilled'), (state) => {
        state.isBusy = false
      })
      .addMatcher((action: AnyAction) => action.type.startsWith('logs/') && action.type.endsWith('/rejected'), (state, action: AnyAction) => {
        state.isBusy = false
        state.status = `Error: ${action.error.message ?? 'Unknown error'}`
      })
  }
})

export const { clearLogs, toggleLogPanel } = logSlice.actions
export default logSlice.reducer
