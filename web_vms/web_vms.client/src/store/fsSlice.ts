import { createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import type { RecordingsQueryDto, RecordingsResponseDto } from '../dto/dtos'
import { parseJson } from './api'

type FsState = {
  recordingsByKey: Record<string, RecordingsResponseDto>
  isBusy: boolean
  status: string
}

const initialState: FsState = {
  recordingsByKey: {},
  isBusy: false,
  status: 'Ready'
}

export const fetchRecordings = createAsyncThunk('fs/fetchRecordings', async (payload: RecordingsQueryDto) => {
  const response = await fetch('/api/fs/recordings', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  })

  return parseJson<RecordingsResponseDto>(response)
})

const fsSlice = createSlice({
  name: 'fs',
  initialState,
  reducers: {},
  extraReducers: (builder) => {
    builder
      .addCase(fetchRecordings.fulfilled, (state, action) => {
        const key = `${action.meta.arg.cameraId}:${action.meta.arg.timestamp}`
        state.recordingsByKey[key] = action.payload
      })
      .addMatcher((action: { type: string }) => action.type.startsWith('fs/') && action.type.endsWith('/pending'), (state) => {
        state.isBusy = true
      })
      .addMatcher((action: { type: string }) => action.type.startsWith('fs/') && action.type.endsWith('/fulfilled'), (state) => {
        state.isBusy = false
      })
      .addMatcher((action: { type: string }) => action.type.startsWith('fs/') && action.type.endsWith('/rejected'), (state, action) => {
        state.isBusy = false
        state.status = `Error: ${action.error.message ?? 'Unknown error'}`
      })
  }
})

export default fsSlice.reducer
