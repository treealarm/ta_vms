import { type AnyAction, createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import type { ApiResponseDto, PlayerCommandDto, PlayerProgressStateDto, PlayerStateDto } from '../dto/dtos'
import { parseJson } from './api'

type ArchState = {
  players: PlayerProgressStateDto[]
  playerStatesByClientId: Record<string, PlayerProgressStateDto>
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
  return parseJson<PlayerProgressStateDto[]>(response)
})

export const addPlayer = createAsyncThunk('arch/addPlayer', async (payload: PlayerStateDto) => {
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
  return parseJson<PlayerProgressStateDto>(response)
})

export const setPlayerState = createAsyncThunk('arch/seekPlayer', async (payload: PlayerStateDto) => {
  const response = await fetch('/api/arch/players/state', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  })

  return parseJson<ApiResponseDto>(response)
})


export const sendPlayerCommand = createAsyncThunk('arch/sendPlayerCommand', async (payload: PlayerCommandDto) => {
  const response = await fetch('/api/arch/players/command', {
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
      .addCase(loadPlayers.fulfilled, (state, action: AnyAction) => {
        state.players = action.payload
        state.status = 'Ready'
      })
      .addCase(fetchPlayerState.fulfilled, (state, action: AnyAction) => {
        const id = action.payload.client_id
        const prev = state.playerStatesByClientId[id]

        const isEqual =
          prev &&
          prev.camera_id === action.payload.camera_id &&
          prev.position_ts === action.payload.position_ts &&
          prev.file_path === action.payload.file_path

        if (!isEqual) {
          state.playerStatesByClientId[id] = action.payload
        }
      })
      .addCase(addPlayer.fulfilled, (state, action: AnyAction) => {
        state.status = `${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
      })
      .addCase(removePlayer.fulfilled, (state, action: AnyAction) => {
        state.status = `${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
      })
      .addCase(setPlayerState.fulfilled, (state, action: AnyAction) => {
        state.status = `${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
      })
      .addCase(sendPlayerCommand.fulfilled, (state, action: AnyAction) => {
        state.status = `${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
      })
      .addMatcher((action: AnyAction) => action.type.startsWith('arch/') && action.type.endsWith('/pending'), (state) => {
        state.isBusy = true
      })
      .addMatcher((action: AnyAction) => action.type.startsWith('arch/') && action.type.endsWith('/fulfilled'), (state) => {
        state.isBusy = false
      })
      .addMatcher(
        (action: AnyAction) => action.type.startsWith('arch/') && action.type.endsWith('/rejected'),
        (state, action: AnyAction) => {
          state.isBusy = false
          state.status = `Error: ${action.error.message ?? 'Unknown error'}`
        }
      )
  }
})

export default archSlice.reducer
