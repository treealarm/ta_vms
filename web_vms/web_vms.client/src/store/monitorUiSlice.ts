import { createSlice, type PayloadAction } from '@reduxjs/toolkit'
import type { RootState } from '.'

type SlotUiState = {
  mode: 'live' | 'archive'
  selectedStream: string
  panelOpen: boolean
  ptzOpen: boolean
}

const DEFAULT_SLOT: SlotUiState = { mode: 'live', selectedStream: '', panelOpen: false, ptzOpen: false }

const monitorUiSlice = createSlice({
  name: 'monitorUi',
  initialState: { slots: {} as Record<string, SlotUiState> },
  reducers: {
    patchSlot(state, { payload }: PayloadAction<{ key: string } & Partial<SlotUiState>>) {
      const { key, ...patch } = payload
      state.slots[key] = { ...(state.slots[key] ?? DEFAULT_SLOT), ...patch }
    },
  },
})

export const { patchSlot } = monitorUiSlice.actions

export const selectSlotUi = (key: string) => (state: RootState): SlotUiState =>
  state.monitorUi.slots[key] ?? DEFAULT_SLOT

export default monitorUiSlice.reducer
