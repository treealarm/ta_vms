import { createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import { parseJson } from './api'

type FrontendConfigDto = {
  squareFrontendOrigin?: string | null
}

type FrontendConfigState = {
  squareFrontendOrigin: string | null
}

const initialState: FrontendConfigState = {
  squareFrontendOrigin: null,
}

export const loadFrontendConfig = createAsyncThunk('frontendConfig/load', async () => {
  const response = await fetch('/api/config/frontend')
  return parseJson<FrontendConfigDto>(response)
})

const frontendConfigSlice = createSlice({
  name: 'frontendConfig',
  initialState,
  reducers: {},
  extraReducers: (builder) => {
    builder.addCase(loadFrontendConfig.fulfilled, (state, action) => {
      state.squareFrontendOrigin = action.payload.squareFrontendOrigin || null
    })
  },
})

export default frontendConfigSlice.reducer
