import { type AnyAction, createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import type { CameraSetupDto, CameraStreamDto, ApiResponseDto, MediaServerNodeDto } from '../dto/dtos'
import { RECORDING_PROFILE_OFF_ID, RECORDING_PROFILE_PREALARM_ID, recordableStreams } from '../dto/dtos'
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
  async ({ cameraId, enabled }: { cameraId: string; enabled: boolean }, { dispatch }) => {
    const response = await fetch(
      `/api/onvif/cameras/${encodeURIComponent(cameraId)}/subscription`,
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ subscribed: enabled }),
      }
    )
    if (response.ok)
      await dispatch(fetchCameraState(cameraId))
  }
)

// One request, not two. This runs on an interval per open card, and the armed flag used to cost a
// second round trip of its own — with the bbox overlays on, their two SSE streams already hold two
// of the six connections a browser allows per origin, so two cameras' worth of paired polls filled
// the pool exactly and everything else on the page queued behind them. The gateway now returns
// `armed` with the rest of the state (see LiveController.GetCameraState).
export const fetchCameraState = createAsyncThunk('live/fetchCameraState', async (cameraId: string) => {
  const cam = await parseJson<CameraSetupDto>(
    await fetch(`/api/live/cameras/${encodeURIComponent(cameraId)}/state`)
  )
  return {
    ...cam,
    recordingMode: cam.recordingMode ?? (cam.recording === true ? 'continuous' : 'off'),
  }
})

export const setArmed = createAsyncThunk(
  'live/setArmed',
  async ({ cameraId, armed }: { cameraId: string; armed: boolean }, { dispatch }) => {
    const response = await fetch(
      `/api/cameras/${encodeURIComponent(cameraId)}/state`,
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ armed }),
      }
    )
    if (response.ok)
      await dispatch(fetchCameraState(cameraId))
  }
)

export const setStreamOverride = createAsyncThunk(
  'live/setStreamOverride',
  async (
    { cameraId, streamName, profileId }: { cameraId: string; streamName: string; profileId: string | null },
    { dispatch }
  ) => {
    const response =
      profileId === null
        ? await fetch(
            `/api/recording-profiles/stream-override?cameraId=${encodeURIComponent(cameraId)}&streamName=${encodeURIComponent(streamName)}`,
            { method: 'DELETE' }
          )
        : await fetch('/api/recording-profiles/stream-override', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ cameraId, streamName, profileId }),
          })
    if (response.ok)
      await dispatch(fetchCameraState(cameraId))
  }
)

export const setStreamRecordingProfile = createAsyncThunk(
  'live/setStreamRecordingProfile',
  async (
    { cameraId, streamName, profileId }: { cameraId: string; streamName: string; profileId: string },
    { dispatch }
  ) => {
    const response = await fetch('/api/recording-profiles/stream-profile', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ cameraId, streamName, profileId }),
    })
    if (response.ok)
      await dispatch(fetchCameraState(cameraId))
  }
)

/// Switches continuous recording for a whole camera: every enabled stream that is available for
/// recording. One request rather than a loop, so the set of streams is decided by the server and
/// cannot disagree with what the "available for recording" flag allows.
export const setCameraContinuousRecording = createAsyncThunk(
  'live/setCameraContinuousRecording',
  async ({ cameraId, on }: { cameraId: string; on: boolean }, { dispatch }) => {
    const response = await fetch('/api/recording-profiles/camera-continuous', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ cameraId, on }),
    })
    if (response.ok)
      await dispatch(fetchCameraState(cameraId))
  }
)

// Arm/disarm plus the prealarm-baseline profile flip, used by the camera card --
// the only place arming is offered, because that is where an operator can see the
// video they are judging: arming puts non-recording streams on the
// prealarm profile (and disarming reverts prealarm back to off). Without a
// baseline profile the stream pipeline is torn down whenever the alarm override
// expires (recording off<->continuous flapping), which kills the MediaMTX push
// mid-stream and the analytics RTSP reader with it.
export const setArmedWithPrealarmBaseline = createAsyncThunk(
  'live/setArmedWithPrealarmBaseline',
  async ({ cameraId, armed }: { cameraId: string; armed: boolean }, { dispatch, getState }) => {
    await dispatch(setArmed({ cameraId, armed }))
    const camera = (getState() as { live: LiveState }).live.cameraStatesById[cameraId]
    const isStreamRecording = (s: CameraStreamDto) =>
      (((s.overrideProfile ?? s.recordingProfile)?.mode) ?? 'off') !== 'off'
    // Only streams allowed into the archive: arming a camera must not be a way past the
    // "available for recording" flag.
    for (const s of recordableStreams(camera ?? {})) {
      if (armed) {
        if (!isStreamRecording(s))
          await dispatch(setStreamRecordingProfile({ cameraId, streamName: s.streamName, profileId: RECORDING_PROFILE_PREALARM_ID }))
      } else if (s.recordingProfile?.id === RECORDING_PROFILE_PREALARM_ID) {
        await dispatch(setStreamRecordingProfile({ cameraId, streamName: s.streamName, profileId: RECORDING_PROFILE_OFF_ID }))
      }
    }
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
