import { type AnyAction, createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import type { ApiResponseDto, RecordingScheduleDto } from '../dto/dtos'
import { parseJson } from './api'

type ScheduleState = {
  schedules: RecordingScheduleDto[]
  status: string
}

const initialState: ScheduleState = {
  schedules: [],
  status: ''
}

export const loadSchedules = createAsyncThunk('schedule/loadSchedules', async () => {
  const response = await fetch('/api/recording-schedules')
  return parseJson<RecordingScheduleDto[]>(response)
})

export const upsertSchedule = createAsyncThunk(
  'schedule/upsertSchedule',
  async (dto: RecordingScheduleDto, { dispatch }) => {
    const response = await fetch('/api/recording-schedules', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(dto)
    })
    const result = await parseJson<ApiResponseDto>(response)
    await dispatch(loadSchedules())
    return result
  }
)

export const removeSchedule = createAsyncThunk(
  'schedule/removeSchedule',
  async (id: string, { dispatch }) => {
    const response = await fetch(`/api/recording-schedules/${encodeURIComponent(id)}`, {
      method: 'DELETE'
    })
    const result = await parseJson<ApiResponseDto>(response)
    await dispatch(loadSchedules())
    return result
  }
)

export const setCameraSchedules = createAsyncThunk(
  'schedule/setCameraSchedules',
  async ({ cameraId, scheduleIds }: { cameraId: string; scheduleIds: string[] }) => {
    const response = await fetch(
      `/api/recording-schedules/cameras/${encodeURIComponent(cameraId)}`,
      {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ scheduleIds })
      }
    )
    return parseJson<ApiResponseDto>(response)
  }
)

const scheduleSlice = createSlice({
  name: 'schedule',
  initialState,
  reducers: {},
  extraReducers: (builder) => {
    builder
      .addCase(loadSchedules.fulfilled, (state, action) => {
        state.schedules = action.payload
      })
      .addMatcher(
        (action: AnyAction) => action.type.startsWith('schedule/') && action.type.endsWith('/rejected'),
        (state, action: AnyAction) => {
          state.status = `Error: ${action.error?.message ?? 'Unknown error'}`
        }
      )
  }
})

export default scheduleSlice.reducer
