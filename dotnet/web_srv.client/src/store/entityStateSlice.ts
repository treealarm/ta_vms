import { type AnyAction, createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import type { EntityStateDto } from '../dto/dtos'
import { parseJson } from './api'

type EntityStateState = {
  entities: EntityStateDto[]
  isBusy: boolean
  status: string
}

const initialState: EntityStateState = {
  entities: [],
  isBusy: false,
  status: 'Ready'
}

export const fetchEntityStates = createAsyncThunk('entityState/fetchEntityStates', async () => {
  const response = await fetch('/api/entitystate')
  return parseJson<EntityStateDto[]>(response)
})

const entityStateSlice = createSlice({
  name: 'entityState',
  initialState,
  reducers: {},
  extraReducers: (builder) => {
    builder
      .addCase(fetchEntityStates.fulfilled, (state, action) => {
        state.entities = action.payload
        state.status = `Loaded ${action.payload.length} entities`
      })
      .addMatcher((action: AnyAction) => action.type.startsWith('entityState/') && action.type.endsWith('/pending'), (state) => {
        state.isBusy = true
      })
      .addMatcher((action: AnyAction) => action.type.startsWith('entityState/') && action.type.endsWith('/fulfilled'), (state) => {
        state.isBusy = false
      })
      .addMatcher((action: AnyAction) => action.type.startsWith('entityState/') && action.type.endsWith('/rejected'), (state, action: AnyAction) => {
        state.isBusy = false
        state.status = `Error: ${action.error.message ?? 'Unknown error'}`
      })
  }
})

export default entityStateSlice.reducer
