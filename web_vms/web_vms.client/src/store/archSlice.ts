import { createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import type { AddPlayerRequestDto, ApiResponseDto, PlayerStateDto, SeekPlayerRequestDto } from '../dto/dtos'
import { parseJson } from './api'

type ArchState = {
  players: PlayerStateDto[]
  playerStatesByClientId: Record<string, PlayerStateDto>
  isBusy: boolean
  status: string
}

const initialState: ArchState = {
  players: [],
  playerStatesByClientId: {},
  isBusy: false,
  status: 'Ready'
}

export const loadPlayers = createAsyncThunk('arch/loadPlayers', async () => {
  const response = await fetch('/api/arch/players')
  return parseJson<PlayerStateDto[]>(response)
})

export const addPlayer = createAsyncThunk('arch/addPlayer', async (payload: AddPlayerRequestDto) => {
  const response = await fetch('/api/arch/players', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  })

  return parseJson<ApiResponseDto>(response)
})

export const removePlayer = createAsyncThunk('arch/removePlayer', async (clientId: string) => {
  const response = await fetch(`/api/arch/players/${encodeURIComponent(clientId)}`, {
    method: 'DELETE'
  })

  return parseJson<ApiResponseDto>(response)
})

export const fetchPlayerState = createAsyncThunk('arch/fetchPlayerState', async (clientId: string) => {
  const response = await fetch(`/api/arch/players/state?clientId=${encodeURIComponent(clientId)}`)
  return parseJson<PlayerStateDto>(response)
})

export const seekPlayer = createAsyncThunk('arch/seekPlayer', async (payload: SeekPlayerRequestDto) => {
  const response = await fetch('/api/arch/players/state', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  })

  return parseJson<ApiResponseDto>(response)
})

const archSlice = createSlice({
  name: 'arch',
  initialState,
  reducers: {},
  extraReducers: (builder) => {
    builder
      .addCase(loadPlayers.fulfilled, (state, action) => {
        state.players = action.payload
      })
      .addCase(fetchPlayerState.fulfilled, (state, action) => {
        const id = action.payload.clientId
        const prev = state.playerStatesByClientId[id]

        const isEqual =
          prev &&
          prev.cameraId === action.payload.cameraId &&
          prev.position === action.payload.position &&
          prev.filePath === action.payload.filePath

        if (!isEqual) {
          state.playerStatesByClientId[id] = action.payload
        }
      })
      .addCase(addPlayer.fulfilled, (state, action) => {
        state.status = `${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
      })
      .addCase(removePlayer.fulfilled, (state, action) => {
        state.status = `${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
      })
      .addCase(seekPlayer.fulfilled, (state, action) => {
        state.status = `${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
      })
      .addMatcher((action: { type: string }) => action.type.startsWith('arch/') && action.type.endsWith('/pending'), (state) => {
        state.isBusy = true
      })
      .addMatcher((action: { type: string }) => action.type.startsWith('arch/') && action.type.endsWith('/fulfilled'), (state) => {
        state.isBusy = false
      })
      .addMatcher(
        (action: { type: string }) => action.type.startsWith('arch/') && action.type.endsWith('/rejected'),
        (state, action) => {
          state.isBusy = false
          state.status = `Error: ${action.error.message ?? 'Unknown error'}`
        }
      )
  }
})

export default archSlice.reducer
