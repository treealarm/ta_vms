import { createAsyncThunk, createSlice, type PayloadAction } from '@reduxjs/toolkit'
import { mapApi } from '../api/mapApi'
import type { MapFigure, MapMarker, MapObjProps } from '../dto/mapDtos'

/** How many children one page of the tree asks for. GetByParent treats count=0 as "none", so a
 *  positive page is always sent. */
export const CHILDREN_PAGE = 1000

export type ChildrenPage = { ids: string[]; loaded: boolean; hasMore: boolean }

export type ObjectsState = {
  markers: Record<string, MapMarker>
  figures: Record<string, MapFigure>
  childrenByParent: Record<string, ChildrenPage>
  /** Null until the roots have been asked for; empty means the tree has no nodes. */
  roots: string[] | null
  props: Record<string, MapObjProps>
  busy: number
  error: string | null
}

const initialState: ObjectsState = {
  markers: {},
  figures: {},
  childrenByParent: {},
  roots: null,
  props: {},
  busy: 0,
  error: null,
}

export const loadRoots = createAsyncThunk('objects/loadRoots', async () => {
  const response = await mapApi.getByParent('', CHILDREN_PAGE)
  return {
    parentId: '',
    parents: response.parents ?? [],
    children: response.children ?? [],
    append: false,
  }
})

export const loadChildren = createAsyncThunk(
  'objects/loadChildren',
  async ({ parentId, startId }: { parentId: string; startId?: string }) => {
    const response = await mapApi.getByParent(parentId, CHILDREN_PAGE, startId)
    return {
      parentId,
      parents: response.parents ?? [],
      children: response.children ?? [],
      append: startId !== undefined,
    }
  },
)

/**
 * The nodes with their geometry: markers for every id, figures for the ones that have a shape.
 * Two calls because a node without a figure still has a name, a parent and a camera id.
 */
export const loadNodes = createAsyncThunk('objects/loadNodes', async (ids: string[]) => {
  const [markers, figures] = await Promise.all([
    mapApi.getMarkersByIds(ids),
    mapApi.getByIds(ids),
  ])

  return { markers: markers.markers ?? [], figures: figures.figs ?? [] }
})

export const loadObjProps = createAsyncThunk(
  'objects/loadObjProps',
  async (id: string) => mapApi.getObjProps(id),
)

function mergeMarker(state: ObjectsState, marker: MapMarker): void {
  const existing = state.markers[marker.id]
  const oldParent = existing?.parentId ?? ''
  const newParent = marker.parentId ?? ''

  // A node that moved between canvases leaves the old children page and joins the new one, so
  // the tree does not show it twice after a watch update.
  if (existing && oldParent !== newParent) {
    const oldPage = state.childrenByParent[oldParent]
    if (oldPage) oldPage.ids = oldPage.ids.filter((id) => id !== marker.id)

    const newPage = state.childrenByParent[newParent]
    if (newPage && !newPage.ids.includes(marker.id)) newPage.ids.push(marker.id)
  }

  state.markers[marker.id] = marker
}

function mergeFigure(state: ObjectsState, figure: MapFigure): void {
  state.figures[figure.id] = figure
  mergeMarker(state, figure)
}

const objectsSlice = createSlice({
  name: 'objects',
  initialState,
  reducers: {
    /** Removes nodes the watch stream reported gone; their children pages are left alone. */
    dropNodes: (state, action: PayloadAction<string[]>) => {
      for (const id of action.payload) {
        const marker = state.markers[id]
        if (marker) {
          const page = state.childrenByParent[marker.parentId ?? '']
          if (page) page.ids = page.ids.filter((childId) => childId !== id)
        }

        delete state.markers[id]
        delete state.figures[id]
        delete state.props[id]
      }
    },
    clearObjectsError: (state) => {
      state.error = null
    },
  },
  extraReducers: (builder) => {
    builder
      .addCase(loadRoots.fulfilled, (state, action) => {
        for (const marker of action.payload.parents) mergeMarker(state, marker)
        for (const marker of action.payload.children) mergeMarker(state, marker)

        state.roots = action.payload.children.map((child) => child.id)
        state.childrenByParent[''] = {
          ids: [...state.roots],
          loaded: true,
          hasMore: action.payload.children.length >= CHILDREN_PAGE,
        }
      })
      .addCase(loadChildren.fulfilled, (state, action) => {
        const { parentId, parents, children, append } = action.payload
        for (const marker of parents) mergeMarker(state, marker)
        for (const marker of children) mergeMarker(state, marker)

        const page = state.childrenByParent[parentId] ?? { ids: [], loaded: false, hasMore: false }
        const ids = append ? page.ids : []
        const known = new Set(ids)
        for (const child of children) {
          if (!known.has(child.id)) {
            ids.push(child.id)
            known.add(child.id)
          }
        }

        state.childrenByParent[parentId] = {
          ids,
          loaded: true,
          hasMore: children.length >= CHILDREN_PAGE,
        }
      })
      .addCase(loadNodes.fulfilled, (state, action) => {
        for (const marker of action.payload.markers) mergeMarker(state, marker)
        for (const figure of action.payload.figures) mergeFigure(state, figure)
      })
      .addCase(loadObjProps.fulfilled, (state, action) => {
        const props = action.payload
        if (props.marker?.id) {
          mergeMarker(state, props.marker)
          state.props[props.marker.id] = props
        }
      })
      .addMatcher(
        (action) => action.type.startsWith('objects/') && action.type.endsWith('/pending'),
        (state) => {
          state.busy += 1
        },
      )
      .addMatcher(
        (action) => action.type.startsWith('objects/') && action.type.endsWith('/fulfilled'),
        (state) => {
          state.busy = Math.max(0, state.busy - 1)
          state.error = null
        },
      )
      .addMatcher(
        (action) => action.type.startsWith('objects/') && action.type.endsWith('/rejected'),
        (state, action) => {
          state.busy = Math.max(0, state.busy - 1)
          state.error = (action as { error?: { message?: string } }).error?.message ?? 'Unknown error'
        },
      )
  },
})

export const { dropNodes, clearObjectsError } = objectsSlice.actions
export default objectsSlice.reducer

/** The markers currently known for a parent, in the order the server returned them. */
export function childrenOf(state: ObjectsState, parentId: string): MapMarker[] {
  const page = state.childrenByParent[parentId]
  if (!page) return []

  return page.ids
    .map((id) => state.markers[id])
    .filter((marker): marker is MapMarker => marker !== undefined)
}
