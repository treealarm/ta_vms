import { type AnyAction, createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import type { MonitorDto, ApiResponseDto } from '../dto/dtos'
import { parseJson } from './api'

type MonitorsState = {
  monitors: MonitorDto[]
  isBusy: boolean
  status: string
}

const initialState: MonitorsState = {
  monitors: [],
  isBusy: false,
  status: 'Ready',
}

export const loadMonitors = createAsyncThunk('monitors/load', async () => {
  const response = await fetch('/api/monitor')
  return parseJson<MonitorDto[]>(response)
})

export const upsertMonitor = createAsyncThunk('monitors/upsert', async (dto: MonitorDto) => {
  const response = await fetch('/api/monitor', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(dto),
  })
  return parseJson<MonitorDto>(response)
})

export const removeMonitor = createAsyncThunk('monitors/remove', async (id: string) => {
  const response = await fetch(`/api/monitor/${encodeURIComponent(id)}`, { method: 'DELETE' })
  return { id, result: await parseJson<ApiResponseDto>(response) }
})

export const setMonitorCameras = createAsyncThunk(
  'monitors/setCameras',
  async ({ monitorId, cameraIds }: { monitorId: string; cameraIds: string[] }) => {
    const response = await fetch(`/api/monitor/${encodeURIComponent(monitorId)}/cameras`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ cameraIds }),
    })
    return { monitorId, cameraIds, result: await parseJson<ApiResponseDto>(response) }
  }
)

export const clearMonitorSlot = createAsyncThunk(
  'monitors/clearSlot',
  async ({ monitorId, slotIndex }: { monitorId: string; slotIndex: number }) => {
    const response = await fetch(
      `/api/monitor/${encodeURIComponent(monitorId)}/slots/${slotIndex}`,
      { method: 'DELETE' }
    )
    return { monitorId, slotIndex, result: await parseJson<ApiResponseDto>(response) }
  }
)

/** Sharing an investigation board hands the recipient an independent copy they then own. */
export const copyMonitorTo = createAsyncThunk(
  'monitors/copyTo',
  async ({ monitorId, username }: { monitorId: string; username: string }) => {
    const response = await fetch(`/api/monitor/${encodeURIComponent(monitorId)}/copy-to`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username }),
    })
    return parseJson<MonitorDto>(response)
  }
)

export const setMonitorSlot = createAsyncThunk(
  'monitors/setSlot',
  async ({ monitorId, slotIndex, cameraId }: { monitorId: string; slotIndex: number; cameraId: string }) => {
    const response = await fetch(
      `/api/monitor/${encodeURIComponent(monitorId)}/slots/${slotIndex}`,
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ cameraId }),
      }
    )
    return { monitorId, slotIndex, cameraId, result: await parseJson<ApiResponseDto>(response) }
  }
)

/** Pins a slot to one stream for this user, or releases it back to automatic with an empty name. */
export const setMonitorSlotStream = createAsyncThunk(
  'monitors/setSlotStream',
  async ({ monitorId, slotIndex, streamName }: { monitorId: string; slotIndex: number; streamName: string }) => {
    const response = await fetch(
      `/api/monitor/${encodeURIComponent(monitorId)}/slots/${slotIndex}/stream`,
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ streamName }),
      }
    )
    return { monitorId, slotIndex, streamName, result: await parseJson<ApiResponseDto>(response) }
  }
)

const monitorsSlice = createSlice({
  name: 'monitors',
  initialState,
  reducers: {},
  extraReducers: (builder) => {
    builder
      .addCase(loadMonitors.fulfilled, (state, action) => {
        state.monitors = action.payload
        state.status = 'Ready'
      })
      .addCase(upsertMonitor.fulfilled, (state, action) => {
        const updated = action.payload
        const idx = state.monitors.findIndex((m) => m.monitorId === updated.monitorId)
        if (idx >= 0) {
          // Preserve camera pool and slots; server only updates name/layout
          state.monitors[idx] = {
            ...state.monitors[idx],
            name: updated.name,
            layout: updated.layout,
          }
        } else {
          state.monitors.push(updated)
        }
        state.status = 'Ready'
      })
      .addCase(removeMonitor.fulfilled, (state, action) => {
        state.monitors = state.monitors.filter((m) => m.monitorId !== action.payload.id)
        state.status = 'Ready'
      })
      .addCase(setMonitorCameras.fulfilled, (state, action) => {
        const { monitorId, cameraIds } = action.payload
        const m = state.monitors.find((x) => x.monitorId === monitorId)
        if (m) {
          m.cameraIds = cameraIds
          // Mirrors the server: only slots whose camera left the pool are dropped, so adding a
          // camera to a board does not blank the cells already placed on it.
          m.slots = m.slots.filter((s) => cameraIds.includes(s.cameraId))
        }
      })
      .addCase(setMonitorSlotStream.fulfilled, (state, action) => {
        const { monitorId, slotIndex, streamName } = action.payload
        const m = state.monitors.find((x) => x.monitorId === monitorId)
        const slot = m?.slots.find((s) => s.slotIndex === slotIndex)
        // Only an existing slot carries a pin: there is nothing to pin a stream on in an empty one.
        if (slot) slot.streamName = streamName
      })
      .addCase(setMonitorSlot.fulfilled, (state, action) => {
        const { monitorId, slotIndex, cameraId } = action.payload
        const m = state.monitors.find((x) => x.monitorId === monitorId)
        if (m) {
          const existingSlot = m.slots.find((s) => s.slotIndex === slotIndex)
          if (existingSlot) existingSlot.cameraId = cameraId
          else m.slots.push({ slotIndex, cameraId })
        }
      })
      .addCase(clearMonitorSlot.fulfilled, (state, action) => {
        const { monitorId, slotIndex } = action.payload
        const m = state.monitors.find((x) => x.monitorId === monitorId)
        if (m) m.slots = m.slots.filter((s) => s.slotIndex !== slotIndex)
      })
      .addMatcher(
        (action: AnyAction) => action.type.startsWith('monitors/') && action.type.endsWith('/pending'),
        (state) => { state.isBusy = true }
      )
      .addMatcher(
        (action: AnyAction) => action.type.startsWith('monitors/') && action.type.endsWith('/fulfilled'),
        (state) => { state.isBusy = false }
      )
      .addMatcher(
        (action: AnyAction) => action.type.startsWith('monitors/') && action.type.endsWith('/rejected'),
        (state, action: AnyAction) => {
          state.isBusy = false
          state.status = `Error: ${action.error?.message ?? 'Unknown error'}`
        }
      )
  },
})

export default monitorsSlice.reducer
