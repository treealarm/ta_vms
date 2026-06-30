import { type AnyAction, createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import type {
  ApiResponseDto,
  RecordingCoverageQueryDto,
  RecordingCoverageResponseDto,
  RecordingRangesResponseDto,
  RecordingsQueryDto,
  RecordingsResponseDto,
  RetentionEventDto
} from '../dto/dtos'
import { parseJson } from './api'

type FsState = {
  recordingsByKey: Record<string, RecordingsResponseDto>
  recordingRangesByKey: Record<string, RecordingRangesResponseDto>
  recordingCoverageByKey: Record<string, RecordingCoverageResponseDto>
  isBusy: boolean
  status: string
  simulationEventId: string
  simulationAlarmStartTs: string
  simulationResult: { ok: boolean; message: string } | null
}

export const recordingRangesKey = (arg: Pick<RecordingsQueryDto, 'cameraId' | 'timestamp' | 'fromUtc' | 'toUtc'>) =>
  `${arg.cameraId}:${arg.timestamp ?? `${arg.fromUtc ?? ''}|${arg.toUtc ?? ''}`}`

export const recordingCoverageKey = (arg: Pick<RecordingCoverageQueryDto, 'cameraId' | 'granularity' | 'fromUtc'>) =>
  `${arg.cameraId}:${arg.granularity}:${arg.fromUtc ?? ''}`

const initialState: FsState = {
  recordingsByKey: {},
  recordingRangesByKey: {},
  recordingCoverageByKey: {},
  isBusy: false,
  status: 'Ready',
  simulationEventId: `test-alarm-${Date.now()}`,
  simulationAlarmStartTs: '',
  simulationResult: null,
}

export const fetchRecordings = createAsyncThunk('fs/fetchRecordings', async (payload: RecordingsQueryDto) => {
  const response = await fetch('/api/fs/recordings', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  })

  return parseJson<RecordingsResponseDto>(response)
})

export const fetchRecordingRanges = createAsyncThunk(
  'fs/fetchRecordingRanges',
  async (payload: RecordingsQueryDto) => {
    const response = await fetch('/api/fs/recording-ranges', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    })

    return parseJson<RecordingRangesResponseDto>(response)
  }
)

export const fetchRecordingCoverage = createAsyncThunk(
  'fs/fetchRecordingCoverage',
  async (payload: RecordingCoverageQueryDto) => {
    const response = await fetch('/api/fs/recording-coverage', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    })

    return parseJson<RecordingCoverageResponseDto>(response)
  }
)

export const sendRetentionEvent = createAsyncThunk('fs/sendRetentionEvent', async (payload: RetentionEventDto) => {
  const response = await fetch('/api/fs/events', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  })

  return parseJson<ApiResponseDto>(response)
})

const fsSlice = createSlice({
  name: 'fs',
  initialState,
  reducers: {
    setSimulationEventId(state, action: { payload: string }) {
      state.simulationEventId = action.payload
      state.simulationResult = null
    },
    setSimulationAlarmStartTs(state, action: { payload: string }) {
      state.simulationAlarmStartTs = action.payload
    },
  },
  extraReducers: (builder) => {
    builder
      .addCase(fetchRecordings.fulfilled, (state, action: AnyAction) => {
        const key = `${action.meta.arg.cameraId}:${action.meta.arg.timestamp ?? ''}`
        state.recordingsByKey[key] = action.payload
      })
      .addCase(fetchRecordingRanges.fulfilled, (state, action: AnyAction) => {
        const key = recordingRangesKey(action.meta.arg as RecordingsQueryDto)
        state.recordingRangesByKey[key] = action.payload
      })
      .addCase(fetchRecordingCoverage.fulfilled, (state, action: AnyAction) => {
        const key = recordingCoverageKey(action.meta.arg as RecordingCoverageQueryDto)
        state.recordingCoverageByKey[key] = action.payload
      })
      .addCase(sendRetentionEvent.fulfilled, (state, action) => {
        state.status = `Retention event: ${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
        state.simulationResult = { ok: action.payload.success, message: action.payload.message }
      })
      .addCase(sendRetentionEvent.rejected, (state, action) => {
        const msg = action.error.message ?? 'Unknown error'
        state.status = `Error: ${msg}`
        state.simulationResult = { ok: false, message: msg }
      })
      .addMatcher((action: AnyAction) => action.type.startsWith('fs/') && action.type.endsWith('/pending'), (state) => {
        state.isBusy = true
      })
      .addMatcher((action: AnyAction) => action.type.startsWith('fs/') && action.type.endsWith('/fulfilled'), (state) => {
        state.isBusy = false
      })
      .addMatcher((action: AnyAction) => action.type.startsWith('fs/') && action.type.endsWith('/rejected'), (state, action: AnyAction) => {
        state.isBusy = false
        state.status = `Error: ${action.error.message ?? 'Unknown error'}`
      })
  }
})

export const { setSimulationEventId, setSimulationAlarmStartTs } = fsSlice.actions
export default fsSlice.reducer
