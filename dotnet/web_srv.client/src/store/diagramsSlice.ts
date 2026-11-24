import { createAsyncThunk, createSlice, type PayloadAction } from '@reduxjs/toolkit'
import { mapApi } from '../api/mapApi'
import type { MapDiagramContent, MapDiagramType, MapValue } from '../dto/mapDtos'

export type DiagramsState = {
  /**
   * The diagrams read, by id. A diagram is left for another while its read is still in flight — a
   * drag opens the element it drops, and the diagram it was dropped on reloads as well — so the
   * answers overlap; each keeps its own entry and the viewer draws the one it is showing.
   */
  content: Record<string, MapDiagramContent>
  /** How many levels of children a diagram draws: the model allows 0–3. */
  depth: number
  /** Values by the node that owns them; a replica appears under its own id. */
  values: Record<string, MapValue[]>
  /** The diagram types, for the editor's pickers; the types dialog writes them. */
  types: MapDiagramType[]
  /** A read in flight, by diagram: leaving one diagram for another overlaps two. */
  loading: Record<string, boolean>
  /** What a diagram's read failed with, by diagram — a failure of the one just left is not shown. */
  error: Record<string, string>
}

const initialState: DiagramsState = {
  content: {},
  depth: 1,
  values: {},
  types: [],
  loading: {},
  error: {},
}

/** A diagram with its children down to `depth` levels, its type and its breadcrumb chain. */
export const loadDiagramContent = createAsyncThunk(
  'diagrams/loadContent',
  async ({ id, depth }: { id: string; depth: number }) => ({
    id,
    content: await mapApi.getDiagramContent(id, depth),
  }),
)

/** The type dictionary: what draws a diagram and the regions its children may take. */
export const loadDiagramTypes = createAsyncThunk('diagrams/loadTypes', async () =>
  (await mapApi.getDiagramTypes({})).diagramTypes ?? [])

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
  },
  extraReducers: (builder) => {
    builder
      .addCase(loadDiagramContent.pending, (state, action) => {
        state.loading[action.meta.arg.id] = true
        delete state.error[action.meta.arg.id]
      })
      .addCase(loadDiagramContent.fulfilled, (state, action) => {
        state.loading[action.payload.id] = false
        state.content[action.payload.id] = action.payload.content
      })
      .addCase(loadDiagramContent.rejected, (state, action) => {
        state.loading[action.meta.arg.id] = false
        state.error[action.meta.arg.id] = action.error.message ?? 'Unknown error'
      })
      .addCase(loadDiagramValues.fulfilled, (state, action) => {
        state.values = action.payload
      })
      .addCase(loadDiagramTypes.fulfilled, (state, action) => {
        state.types = action.payload
      })
  },
})

export const { setDiagramDepth } = diagramsSlice.actions
export default diagramsSlice.reducer
