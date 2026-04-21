import { type AnyAction, createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import type { LogEntryDto, LogQueryDto, LogResponseDto } from '../dto/dtos'
import { parseJson } from './api'

type LogState = {
  entries: LogEntryDto[]
  hasMore: boolean
  isBusy: boolean
  status: string
  lastQuery: LogQueryDto | null
}

const initialState: LogState = {
  entries: [],
  hasMore: false,
  isBusy: false,
  status: 'Ready',
  lastQuery: null
}

export const fetchLogs = createAsyncThunk('logs/fetchLogs', async (payload: LogQueryDto) => {
  const response = await fetch('/api/logger/logs', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  })

  return parseJson<LogResponseDto>(response)
})

const logSlice = createSlice({
  name: 'logs',
  initialState,
  reducers: {
    clearLogs(state) {
      state.entries = []
      state.hasMore = false
      state.status = 'Ready'
    }
  },
  extraReducers: (builder) => {
    builder
      .addCase(fetchLogs.fulfilled, (state, action: AnyAction) => {
        state.entries = action.payload.logs
        state.hasMore = action.payload.hasMore
        state.lastQuery = action.meta.arg
        state.status = `Loaded ${action.payload.logs.length} logs`
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

export const { clearLogs } = logSlice.actions
export default logSlice.reducer
