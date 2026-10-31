import { createAsyncThunk, createSlice, type PayloadAction } from '@reduxjs/toolkit'
import { mapApi } from '../api/mapApi'
import type { MapDiagramContent, MapValue } from '../dto/mapDtos'

export type DiagramsState = {
  /** The last diagram read; the viewer draws it only when it is the one it asked for. */
  content: MapDiagramContent | null
  /** How many levels of children a diagram draws: the model allows 0–3. */
  depth: number
  /** Values by the node that owns them; a replica appears under its own id. */
  values: Record<string, MapValue[]>
  loading: boolean
  error: string | null
}

const initialState: DiagramsState = {
  content: null,
  depth: 1,
  values: {},
  loading: false,
  error: null,
}

/** A diagram with its children down to `depth` levels, its type and its breadcrumb chain. */
export const loadDiagramContent = createAsyncThunk(
  'diagrams/loadContent',
  async ({ id, depth }: { id: string; depth: number }) => ({
    id,
    content: await mapApi.getDiagramContent(id, depth),
  }),
)

/** The live values of every node of the open diagram, grouped by their owner. */
export const loadDiagramValues = createAsyncThunk(
  'diagrams/loadValues',
  async (ids: string[]) => {
    const response = await mapApi.getValuesByOwners(ids)
    const byOwner: Record<string, MapValue[]> = {}
    for (const value of response.values ?? []) {
      if (!value.ownerId) continue
      const known = byOwner[value.ownerId] ?? []
      known.push(value)
      byOwner[value.ownerId] = known
    }

    return byOwner
  },
)

const diagramsSlice = createSlice({
  name: 'diagrams',
  initialState,
  reducers: {
    setDiagramDepth: (state, action: PayloadAction<number>) => {
      state.depth = action.payload
    },
    clearDiagram: (state) => {
      state.content = null
      state.values = {}
      state.error = null
    },
  },
  extraReducers: (builder) => {
    builder
      .addCase(loadDiagramContent.pending, (state) => {
        state.loading = true
        state.error = null
      })
      .addCase(loadDiagramContent.fulfilled, (state, action) => {
        state.loading = false
        state.content = action.payload.content
      })
      .addCase(loadDiagramContent.rejected, (state, action) => {
        state.loading = false
        state.error = action.error.message ?? 'Unknown error'
      })
      .addCase(loadDiagramValues.fulfilled, (state, action) => {
        state.values = action.payload
      })
  },
})

export const { setDiagramDepth, clearDiagram } = diagramsSlice.actions
export default diagramsSlice.reducer
