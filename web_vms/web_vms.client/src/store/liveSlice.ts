import { type AnyAction, createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import type { CameraSetupDto, ApiResponseDto, MediaServerNodeDto } from '../dto/dtos'
import { parseJson } from './api'

type LiveState = {
  cameraStatesById: Record<string, CameraSetupDto>
  servers: MediaServerNodeDto[]
  isBusy: boolean
  status: string
}

const initialState: LiveState = {
  cameraStatesById: {},
  servers: [],
  isBusy: false,
  status: 'Ready'
}

export const loadServers = createAsyncThunk('live/loadServers', async () => {
  const response = await fetch('/api/live/servers')
  return parseJson<MediaServerNodeDto[]>(response)
})

export const loadCameras = createAsyncThunk('live/loadCameras', async () => {
  const response = await fetch('/api/live/cameras')
  const list = await parseJson<CameraSetupDto[]>(response)
  return list.map((cam) => ({
    ...cam,
    recordingMode: cam.recordingMode ?? (cam.recording === true ? 'continuous' : 'off')
  }))
})

export const addCamera = createAsyncThunk('live/addCamera', async (payload: CameraSetupDto) => {
  const response = await fetch('/api/live/cameras', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  })

  return parseJson<ApiResponseDto>(response)
})

export const removeCamera = createAsyncThunk(
  'live/removeCamera',
  async ({ cameraId }: { cameraId: string; }) => {
    const response = await fetch(
      `/api/live/cameras/${encodeURIComponent(cameraId)}`,
      { method: 'DELETE' }
    )

    return parseJson<ApiResponseDto>(response)
  }
)

export const changeCameraParent = createAsyncThunk(
  'live/changeCameraParent',
  async (payload: CameraSetupDto) => {
    const response = await fetch(
      `/api/live/cameras/${encodeURIComponent(payload.cameraId)}/parent`,
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      }
    )

    return parseJson<ApiResponseDto>(response)
  }
)

export const setOnvifEventsEnabled = createAsyncThunk(
  'live/setOnvifEventsEnabled',
  async ({ camera, enabled }: { camera: CameraSetupDto; enabled: boolean }, { dispatch }) => {
    const result = await dispatch(addCamera({ ...camera, onvifEventsEnabled: enabled }))
    if (!addCamera.rejected.match(result))
      await dispatch(fetchCameraState(camera.cameraId))
  }
)

export const fetchCameraState = createAsyncThunk('live/fetchCameraState', async (cameraId: string) => {
  const response = await fetch(`/api/live/cameras/${encodeURIComponent(cameraId)}/state`)
  const cam = await parseJson<CameraSetupDto>(response)
  return {
    ...cam,
    recordingMode: cam.recordingMode ?? (cam.recording === true ? 'continuous' : 'off')
  }
})


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
        state.cameraStatesById = {}

        for (const cam of action.payload) {
          state.cameraStatesById[cam.cameraId] = cam
        }

        state.status = 'Ready'
      })
      .addCase(loadServers.fulfilled, (state, action) => {
        state.servers = action.payload
      })
      .addCase(fetchCameraState.fulfilled, (state, action) => {
        state.cameraStatesById[action.payload.cameraId] = action.payload
      })
      .addCase(addCamera.fulfilled, (state, action) => {
        state.status = `${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
      })
      .addCase(removeCamera.fulfilled, (state, action) => {
        state.status = `${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
      })
      .addCase(changeCameraParent.fulfilled, (state, action) => {
        state.status = `${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
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
