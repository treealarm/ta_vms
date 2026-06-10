import { type AnyAction, createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import type { ApiResponseDto, CameraStorageAssignmentDto, StorageDto } from '../dto/dtos'
import { parseJson } from './api'

type StorageState = {
  storages: StorageDto[]
  cameraAssignments: Record<string, CameraStorageAssignmentDto[]>
  status: string
}

const initialState: StorageState = {
  storages: [],
  cameraAssignments: {},
  status: ''
}

export const loadStorages = createAsyncThunk('storage/loadStorages', async () => {
  const response = await fetch('/api/Storage')
  return parseJson<StorageDto[]>(response)
})

export const addStorage = createAsyncThunk('storage/addStorage', async (dto: StorageDto) => {
  const response = await fetch('/api/Storage', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(dto)
  })
  return parseJson<ApiResponseDto>(response)
})

export const removeStorage = createAsyncThunk('storage/removeStorage', async (storageId: string) => {
  const response = await fetch(`/api/Storage/${encodeURIComponent(storageId)}`, { method: 'DELETE' })
  return parseJson<ApiResponseDto>(response)
})

export const updateStorage = createAsyncThunk('storage/updateStorage', async (dto: StorageDto) => {
  const response = await fetch(`/api/Storage/${encodeURIComponent(dto.storageId)}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(dto)
  })
  return parseJson<ApiResponseDto>(response)
})

export const loadCameraStorages = createAsyncThunk(
  'storage/loadCameraStorages',
  async (cameraId: string) => {
    const response = await fetch(`/api/Storage/get_camera_storages/${encodeURIComponent(cameraId)}`)
    const assignments = await parseJson<CameraStorageAssignmentDto[]>(response)
    return { cameraId, assignments }
  }
)

export const setCameraStorages = createAsyncThunk(
  'storage/setCameraStorages',
  async ({ cameraId, assignments }: { cameraId: string; assignments: CameraStorageAssignmentDto[] }) => {
    const response = await fetch(`/api/Storage/set_camera_storages/${encodeURIComponent(cameraId)}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(assignments)
    })
    return parseJson<ApiResponseDto>(response)
  }
)

const storageSlice = createSlice({
  name: 'storage',
  initialState,
  reducers: {},
  extraReducers: (builder) => {
    builder
      .addCase(loadStorages.fulfilled, (state, action) => {
        state.storages = action.payload
      })
      .addCase(loadCameraStorages.fulfilled, (state, action) => {
        state.cameraAssignments[action.payload.cameraId] = action.payload.assignments
      })
      .addMatcher(
        (action: AnyAction) => action.type.startsWith('storage/') && action.type.endsWith('/rejected'),
        (state, action: AnyAction) => {
          state.status = `Error: ${action.error?.message ?? 'Unknown error'}`
        }
      )
  }
})

export default storageSlice.reducer
