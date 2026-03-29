import { type AnyAction, createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import type { AddCameraRequestDto, ApiResponseDto, CameraStateDto, UpdateCameraStateRequestDto } from '../dto/dtos'
import { parseJson } from './api'

type LiveState = {
  cameras: CameraStateDto[]
  cameraStatesById: Record<string, CameraStateDto>
  isBusy: boolean
  status: string
}

const initialState: LiveState = {
  cameras: [],
  cameraStatesById: {},
  isBusy: false,
  status: 'Ready'
}

export const loadCameras = createAsyncThunk('live/loadCameras', async () => {
  const response = await fetch('/api/live/cameras')
  return parseJson<CameraStateDto[]>(response)
})

export const addCamera = createAsyncThunk('live/addCamera', async (payload: AddCameraRequestDto) => {
  const response = await fetch('/api/live/cameras', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  })

  return parseJson<ApiResponseDto>(response)
})

export const removeCamera = createAsyncThunk(
  'live/removeCamera',
  async ({ cameraId, isArchive }: { cameraId: string; isArchive: boolean }) => {
    const response = await fetch(
      `/api/live/cameras/${encodeURIComponent(cameraId)}?isArchive=${isArchive}`,
      { method: 'DELETE' }
    )

    return parseJson<ApiResponseDto>(response)
  }
)

export const fetchCameraState = createAsyncThunk('live/fetchCameraState', async (cameraId: string) => {
  const response = await fetch(`/api/live/cameras/${encodeURIComponent(cameraId)}/state`)
  return parseJson<CameraStateDto>(response)
})

export const updateCameraRecording = createAsyncThunk(
  'live/updateCameraRecording',
  async (payload: UpdateCameraStateRequestDto) => {
    const response = await fetch('/api/live/cameras/state', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    })

    return parseJson<ApiResponseDto>(response)
  }
)

const liveSlice = createSlice({
  name: 'live',
  initialState,
  reducers: {
    setLiveStatus(state, action: { payload: string }) {
      state.status = action.payload
    }
  },
  extraReducers: (builder) => {
    builder
      .addCase(loadCameras.fulfilled, (state, action) => {
        state.cameras = action.payload
        state.status = 'Ready'
      })
      .addCase(fetchCameraState.fulfilled, (state, action) => {
        const id = action.payload.cameraId
        const prev = state.cameraStatesById[id]

        const isEqual =
          prev &&
          prev.recording === action.payload.recording &&
          prev.webRtc === action.payload.webRtc &&
          prev.rtspUrl === action.payload.rtspUrl

        if (!isEqual) {
          state.cameraStatesById[id] = action.payload
        }
      })
      .addCase(addCamera.fulfilled, (state, action) => {
        state.status = `${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
      })
      .addCase(removeCamera.fulfilled, (state, action) => {
        state.status = `${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
      })
      .addCase(updateCameraRecording.fulfilled, (state, action) => {
        state.status = `Result: ${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
      })
      .addMatcher((action: AnyAction) => action.type.startsWith('live/') && action.type.endsWith('/pending'), (state) => {
        state.isBusy = true
      })
      .addMatcher((action: AnyAction) => action.type.startsWith('live/') && action.type.endsWith('/fulfilled'), (state) => {
        state.isBusy = false
      })
      .addMatcher(
        (action: AnyAction) => action.type.startsWith('live/') && action.type.endsWith('/rejected'),
        (state, action: AnyAction) => {
          state.isBusy = false
          state.status = `Error: ${action.error?.message ?? 'Unknown error'}`
        }
      )
  }
})

export const { setLiveStatus } = liveSlice.actions
export default liveSlice.reducer
