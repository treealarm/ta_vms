import { type AnyAction, createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import type { ApiResponseDto, DeploymentDto, DeploymentNodeDto } from '../dto/dtos'
import { parseJson } from './api'

type DeploymentState = {
  deployment: DeploymentDto | null
  nodes: DeploymentNodeDto[]
  isBusy: boolean
  status: string
}

const initialState: DeploymentState = {
  deployment: null,
  nodes: [],
  isBusy: false,
  status: 'Ready',
}

// The API answers a refusal as {"error": "..."}; that sentence is what the administrator has to
// act on (cameras still on the node, a name taken), so it is shown as it is.
const failure = async (response: Response): Promise<never> => {
  const text = await response.text()
  let message = text || response.statusText
  try { message = (JSON.parse(text) as { error?: string }).error ?? message } catch { /* not JSON */ }
  throw new Error(message)
}

export const loadDeployment = createAsyncThunk('deployment/load', async () => {
  const response = await fetch('/api/deployment')
  return parseJson<DeploymentDto>(response)
})

export const loadNodes = createAsyncThunk('deployment/loadNodes', async () => {
  const response = await fetch('/api/deployment/nodes')
  return parseJson<DeploymentNodeDto[]>(response)
})

export const setEnrollmentOpen = createAsyncThunk('deployment/setEnrollment', async (open: boolean) => {
  const response = await fetch('/api/deployment/enrollment', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ open }),
  })
  return parseJson<ApiResponseDto>(response)
})

export const removeNode = createAsyncThunk('deployment/remove', async (serverId: string) => {
  const response = await fetch(`/api/deployment/nodes/${encodeURIComponent(serverId)}`, { method: 'DELETE' })
  if (!response.ok) await failure(response)
  return (await response.json()) as ApiResponseDto
})

// Registers the node and saves its package, node-<name>.tar.gz, as a download.
export const downloadNodePackage = createAsyncThunk(
  'deployment/package',
  async (payload: { name: string; address: string; mediaMtx: boolean }) => {
    const response = await fetch('/api/deployment/nodes/package', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    })
    if (!response.ok) await failure(response)
    const url = URL.createObjectURL(await response.blob())
    const a = document.createElement('a')
    a.href = url
    a.download = `node-${payload.name}.tar.gz`
    a.click()
    URL.revokeObjectURL(url)
    return payload.name
  }
)

const deploymentSlice = createSlice({
  name: 'deployment',
  initialState,
  reducers: {},
  extraReducers: (builder) => {
    builder
      .addCase(loadDeployment.fulfilled, (state, action) => {
        state.deployment = action.payload
      })
      .addCase(loadNodes.fulfilled, (state, action) => {
        state.nodes = action.payload
        state.status = 'Ready'
      })
      .addCase(downloadNodePackage.fulfilled, (state, action) => {
        state.status = `OK: node-${action.payload}.tar.gz`
      })
      .addMatcher(
        (action: AnyAction) => action.type.startsWith('deployment/') && action.type.endsWith('/pending'),
        (state) => { state.isBusy = true }
      )
      .addMatcher(
        (action: AnyAction) => action.type.startsWith('deployment/') && action.type.endsWith('/fulfilled'),
        (state) => { state.isBusy = false }
      )
      .addMatcher(
        (action: AnyAction) => action.type.startsWith('deployment/') && action.type.endsWith('/rejected'),
        (state, action: AnyAction) => {
          state.isBusy = false
          state.status = `Error: ${action.error?.message ?? 'Unknown error'}`
        }
      )
  },
})

export default deploymentSlice.reducer
