import { type AnyAction, createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import type { ApiResponseDto, MediaMtxServerDto } from '../dto/dtos'
import { parseJson } from './api'

type MediaMtxState = {
  servers: MediaMtxServerDto[]
  isBusy: boolean
  status: string
}

const initialState: MediaMtxState = {
  servers: [],
  isBusy: false,
  status: 'Ready',
}

export const loadMediaMtxServers = createAsyncThunk('mediaMtx/load', async () => {
  const response = await fetch('/api/media-mtx/servers')
  return parseJson<MediaMtxServerDto[]>(response)
})

export const upsertMediaMtxServer = createAsyncThunk(
  'mediaMtx/upsert',
  async (payload: MediaMtxServerDto) => {
    const response = await fetch('/api/media-mtx/servers', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    })
    return parseJson<ApiResponseDto>(response)
  }
)

export const removeMediaMtxServer = createAsyncThunk(
  'mediaMtx/remove',
  async (id: string) => {
    const response = await fetch(`/api/media-mtx/servers/${encodeURIComponent(id)}`, {
      method: 'DELETE',
    })
    return parseJson<ApiResponseDto>(response)
  }
)

const mediaMtxSlice = createSlice({
  name: 'mediaMtx',
  initialState,
  reducers: {},
  extraReducers: (builder) => {
    builder
      .addCase(loadMediaMtxServers.fulfilled, (state, action) => {
        state.servers = action.payload
        state.status = 'Ready'
      })
      .addCase(upsertMediaMtxServer.fulfilled, (state, action) => {
        state.status = `${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
      })
      .addCase(removeMediaMtxServer.fulfilled, (state, action) => {
        state.status = `${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
      })
      .addMatcher(
        (action: AnyAction) => action.type.startsWith('mediaMtx/') && action.type.endsWith('/pending'),
        (state) => { state.isBusy = true }
      )
      .addMatcher(
        (action: AnyAction) => action.type.startsWith('mediaMtx/') && action.type.endsWith('/fulfilled'),
        (state) => { state.isBusy = false }
      )
      .addMatcher(
        (action: AnyAction) => action.type.startsWith('mediaMtx/') && action.type.endsWith('/rejected'),
        (state, action: AnyAction) => {
          state.isBusy = false
          state.status = `Error: ${action.error?.message ?? 'Unknown error'}`
        }
      )
  },
})

export default mediaMtxSlice.reducer
