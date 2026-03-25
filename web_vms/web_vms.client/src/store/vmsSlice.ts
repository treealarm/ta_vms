import { createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import type {
  AddCameraRequestDto,
  AddPlayerRequestDto,
  ApiResponseDto,
  CameraStateDto,
  PlayerStateDto,
  RecordingsQueryDto,
  RecordingsResponseDto,
  SeekPlayerRequestDto,
  UpdateCameraStateRequestDto
} from '../dto/dtos'

const getErrorMessage = async (response: Response): Promise<string> => {
  const text = await response.text()
  return text || response.statusText
}

const parseJson = async <T>(response: Response): Promise<T> => {
  if (!response.ok) {
    throw new Error(await getErrorMessage(response))
  }

  return response.json() as Promise<T>
}

export const loadCameras = createAsyncThunk('vms/loadCameras', async () => {
  const response = await fetch('/api/live/cameras')
  return parseJson<CameraStateDto[]>(response)
})

export const loadPlayers = createAsyncThunk('vms/loadPlayers', async () => {
  const response = await fetch('/api/arch/players')
  return parseJson<PlayerStateDto[]>(response)
})

export const addCamera = createAsyncThunk('vms/addCamera', async (payload: AddCameraRequestDto) => {
  const response = await fetch('/api/live/cameras', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  })

  return parseJson<ApiResponseDto>(response)
})

export const removeCamera = createAsyncThunk(
  'vms/removeCamera',
  async ({ cameraId, isArchive }: { cameraId: string; isArchive: boolean }) => {
    const response = await fetch(
      `/api/live/cameras/${encodeURIComponent(cameraId)}?isArchive=${isArchive}`,
      { method: 'DELETE' }
    )

    return parseJson<ApiResponseDto>(response)
  }
)

export const addPlayer = createAsyncThunk('vms/addPlayer', async (payload: AddPlayerRequestDto) => {
  const response = await fetch('/api/arch/players', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  })

  return parseJson<ApiResponseDto>(response)
})

export const removePlayer = createAsyncThunk('vms/removePlayer', async (clientId: string) => {
  const response = await fetch(`/api/arch/players/${encodeURIComponent(clientId)}`, {
    method: 'DELETE'
  })

  return parseJson<ApiResponseDto>(response)
})

export const fetchCameraState = createAsyncThunk('vms/fetchCameraState', async (cameraId: string) => {
  const response = await fetch(`/api/live/cameras/${encodeURIComponent(cameraId)}/state`)
  return parseJson<CameraStateDto>(response)
})

export const updateCameraRecording = createAsyncThunk(
  'vms/updateCameraRecording',
  async (payload: UpdateCameraStateRequestDto) => {
    const response = await fetch('/api/live/cameras/state', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload)
    })

    return parseJson<ApiResponseDto>(response)
  }
)

export const fetchPlayerState = createAsyncThunk('vms/fetchPlayerState', async (clientId: string) => {
  const response = await fetch(`/api/arch/players/state?clientId=${encodeURIComponent(clientId)}`)
  return parseJson<PlayerStateDto>(response)
})

export const seekPlayer = createAsyncThunk('vms/seekPlayer', async (payload: SeekPlayerRequestDto) => {
  const response = await fetch('/api/arch/players/state', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  })

  return parseJson<ApiResponseDto>(response)
})

export const fetchRecordings = createAsyncThunk('vms/fetchRecordings', async (payload: RecordingsQueryDto) => {
  const response = await fetch('/api/fs/recordings', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  })

  return parseJson<RecordingsResponseDto>(response)
})

type VmsState = {
  cameras: CameraStateDto[]
  players: PlayerStateDto[]
  cameraStatesById: Record<string, CameraStateDto>
  playerStatesByClientId: Record<string, PlayerStateDto>
  recordingsByKey: Record<string, RecordingsResponseDto>
  isBusy: boolean
  status: string
}

const initialState: VmsState = {
  cameras: [],
  players: [],
  cameraStatesById: {},
  playerStatesByClientId: {},
  recordingsByKey: {},
  isBusy: false,
  status: 'Ready'
}

const actionPendingMatcher = (action: { type: string }) =>
  action.type.startsWith('vms/') && action.type.endsWith('/pending')

const actionRejectedMatcher = (action: { type: string }) =>
  action.type.startsWith('vms/') && action.type.endsWith('/rejected')

const actionFulfilledMatcher = (action: { type: string }) =>
  action.type.startsWith('vms/') && action.type.endsWith('/fulfilled')

const vmsSlice = createSlice({
  name: 'vms',
  initialState,
  reducers: {
    clearStatus(state) {
      state.status = 'Ready'
    },
    setStatus(state, action: { payload: string }) {
      state.status = action.payload
    }
  },
  extraReducers: (builder) => {
    builder
      .addCase(loadCameras.fulfilled, (state, action) => {
        state.cameras = action.payload
      })
      .addCase(loadPlayers.fulfilled, (state, action) => {
        state.players = action.payload
      })
      .addCase(fetchCameraState.fulfilled, (state, action) => {
        state.cameraStatesById[action.payload.cameraId] = action.payload
      })
      .addCase(fetchPlayerState.fulfilled, (state, action) => {
        state.playerStatesByClientId[action.payload.clientId] = action.payload
      })
      .addCase(fetchRecordings.fulfilled, (state, action) => {
        const key = `${action.meta.arg.cameraId}:${action.meta.arg.timestamp}`
        state.recordingsByKey[key] = action.payload
      })
      .addCase(addCamera.fulfilled, (state, action) => {
        state.status = `${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
      })
      .addCase(addPlayer.fulfilled, (state, action) => {
        state.status = `${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
      })
      .addCase(removeCamera.fulfilled, (state, action) => {
        state.status = `${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
      })
      .addCase(removePlayer.fulfilled, (state, action) => {
        state.status = `${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
      })
      .addCase(updateCameraRecording.fulfilled, (state, action) => {
        state.status = `Result: ${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
      })
      .addCase(seekPlayer.fulfilled, (state, action) => {
        state.status = `${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
      })
      .addMatcher(actionPendingMatcher, (state) => {
        state.isBusy = true
      })
      .addMatcher(actionFulfilledMatcher, (state) => {
        state.isBusy = false
      })
      .addMatcher(actionRejectedMatcher, (state, action) => {
        state.isBusy = false
        state.status = `Error: ${action.error.message ?? 'Unknown error'}`
      })
  }
})

export const { clearStatus, setStatus } = vmsSlice.actions

export default vmsSlice.reducer
