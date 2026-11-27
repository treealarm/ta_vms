import { type AnyAction, createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import type { MonitorDto } from '../dto/dtos'
import { errorText } from '../api/requestJson'
import { parseApiResponse, parseJson } from './api'

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
  return { id, result: await parseApiResponse(response) }
})

export const setMonitorCameras = createAsyncThunk(
  'monitors/setCameras',
  async ({ monitorId, cameraIds }: { monitorId: string; cameraIds: string[] }) => {
    const response = await fetch(`/api/monitor/${encodeURIComponent(monitorId)}/cameras`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ cameraIds }),
    })
    return { monitorId, cameraIds, result: await parseApiResponse(response) }
  }
)

export const clearMonitorSlot = createAsyncThunk(
  'monitors/clearSlot',
  async ({ monitorId, slotIndex }: { monitorId: string; slotIndex: number }) => {
    const response = await fetch(
      `/api/monitor/${encodeURIComponent(monitorId)}/slots/${slotIndex}`,
      { method: 'DELETE' }
    )
    return { monitorId, slotIndex, result: await parseApiResponse(response) }
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
    return { monitorId, slotIndex, cameraId, result: await parseApiResponse(response) }
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
    return { monitorId, slotIndex, streamName, result: await parseApiResponse(response) }
  }
)

export type RestoreMonitorResult = {
  /** The board that was created, or '' when even that step refused. */
  monitorId: string
  /** The step that refused; absent when the whole board came back. */
  failedStep?: 'monitor' | 'cameras' | 'slots' | 'streams'
  error?: string
}

/**
 * Puts a deleted investigation board back by rebuilding its contents: name and layout, camera
 * pool, slots, and the streams this user had pinned. The copy has a new monitorId — the content
 * is restored, the identity is not.
 *
 * The sequence is not atomic, so a refusal names the step instead of throwing: the caller decides
 * whether to remove the partial board again or to keep it and say what was restored.
 */
export const restoreMonitor = createAsyncThunk<RestoreMonitorResult, MonitorDto>(
  'monitors/restore',
  async (dto, { dispatch }) => {
    const created = await dispatch(upsertMonitor({ ...dto, monitorId: '', cameraIds: [], slots: [] }))
    if (!upsertMonitor.fulfilled.match(created))
      return { monitorId: '', failedStep: 'monitor', error: errorText(created.error) }
    const monitorId = created.payload.monitorId

    const cameras = await dispatch(setMonitorCameras({ monitorId, cameraIds: dto.cameraIds }))
    if (!setMonitorCameras.fulfilled.match(cameras))
      return { monitorId, failedStep: 'cameras', error: errorText(cameras.error) }

    for (const slot of dto.slots) {
      const result = await dispatch(
        setMonitorSlot({ monitorId, slotIndex: slot.slotIndex, cameraId: slot.cameraId }),
      )
      if (!setMonitorSlot.fulfilled.match(result))
        return { monitorId, failedStep: 'slots', error: errorText(result.error) }
    }

    for (const slot of dto.slots) {
      if (!slot.streamName) continue
      const result = await dispatch(
        setMonitorSlotStream({ monitorId, slotIndex: slot.slotIndex, streamName: slot.streamName }),
      )
      if (!setMonitorSlotStream.fulfilled.match(result))
        return { monitorId, failedStep: 'streams', error: errorText(result.error) }
    }

    return { monitorId }
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
