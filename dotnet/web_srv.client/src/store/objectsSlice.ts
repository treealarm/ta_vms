import { createAsyncThunk, createSlice, type PayloadAction } from '@reduxjs/toolkit'
import { mapApi } from '../api/mapApi'
import type { MapFigure, MapMarker, MapObjProps } from '../dto/mapDtos'

/** How many children one page of the tree asks for. GetByParent treats count=0 as "none", so a
 *  positive page is always sent. */
export const CHILDREN_PAGE = 1000

export type ChildrenPage = { ids: string[]; loaded: boolean; hasMore: boolean }

/** What is drawn on one canvas: every figure whose coordinates are the space's, at any depth. */
export type CanvasPage = { ids: string[]; loaded: boolean }

export type ObjectsState = {
  markers: Record<string, MapMarker>
  figures: Record<string, MapFigure>
  childrenByParent: Record<string, ChildrenPage>
  /** The composition of a space's canvas, keyed by the space id. */
  canvases: Record<string, CanvasPage>
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
  canvases: {},
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
 * One canvas whole: every figure drawn in the space's coordinates, whatever its depth in the
 * tree. A figure carries its node's name, parent and camera, so the response fills the markers
 * as well -- the canvas never asks for the tree.
 */
export const loadCanvas = createAsyncThunk('objects/loadCanvas', async (canvasId: string) => {
  const response = await mapApi.getByBox({ canvasId })
  return { canvasId, figures: response.figs ?? [] }
})

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

/**
 * The node behind a camera id. A placement names the camera, not the node, and the node id is a
 * one-way hash of the camera id; when the camera's space has not been read yet, its children are
 * loaded first so the answer can be found in the tree.
 */
export const resolveCameraNode = createAsyncThunk(
  'objects/resolveCameraNode',
  async ({ cameraId, spaceId }: { cameraId: string; spaceId?: string }, api) => {
    const find = () => {
      const known = Object.values((api.getState() as { objects: ObjectsState }).objects.markers)
      // A camera on a canvas is a replica of its canonical node; prefer the one drawn here.
      return known.find((marker) => marker.cameraId === cameraId && !!marker.ownerId)
        ?? known.find((marker) => marker.cameraId === cameraId)
        ?? null
    }

    const known = find()
    if (known) return known

    if (spaceId) await api.dispatch(loadCanvas(spaceId)).unwrap()
    return find()
  },
)

/**
 * Brings a node found by search into view in the tree: reads the chain from the root down to its
 * parent and loads every page along it, since expanding an ancestor shows nothing until its own
 * page is in the tree. Returns the chain, root first, for the caller to expand.
 */
export const revealNode = createAsyncThunk(
  'objects/revealNode',
  async (node: MapMarker, api) => {
    const parentId = node.parentId ?? ''
    const page = await api.dispatch(loadChildren({ parentId })).unwrap()
    const chain = page.parents ?? []

    // The page of every ancestor, so that each one has the row the chain continues through.
    const known = (api.getState() as { objects: ObjectsState }).objects.childrenByParent
    const missing = [...new Set([parentId, ...chain.map((ancestor) => ancestor.parentId ?? '')])]
      .filter((id) => id !== parentId && !known[id]?.loaded)
    await Promise.all(missing.map((id) => api.dispatch(loadChildren({ parentId: id }))))

    return chain
  },
)

/**
 * The one way a node is brought into view in a tree: read the node itself when this store has not
 * seen it, then every page from the root down to it. Answers the node and the chain of ancestors
 * for the caller to unfold — the tree widgets differ in what they do with the chain (a tab, a
 * scroll, a selection), not in what has to be read first.
 */
export const revealNodePath = createAsyncThunk(
  'objects/revealNodePath',
  async (node: MapMarker | string, api): Promise<{
    marker: MapMarker | null
    ancestors: MapMarker[]
  }> => {
    const id = typeof node === 'string' ? node : node.id
    let marker: MapMarker | undefined = typeof node === 'string'
      ? (api.getState() as { objects: ObjectsState }).objects.markers[id]
      : node

    if (!marker) {
      const loaded = await api.dispatch(loadNodes([id])).unwrap().catch(() => null)
      marker = loaded?.markers.find((candidate) => candidate.id === id)
    }
    if (!marker) return { marker: null, ancestors: [] }

    const ancestors = await api
      .dispatch(revealNode(marker))
      .unwrap()
      .catch(() => [] as MapMarker[])
    return { marker, ancestors }
  },
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

  // A node the watch stream has just reported joins its parent's page if that page is open;
  // otherwise the tree would only show it after a reload.
  if (!existing) {
    const page = state.childrenByParent[newParent]
    if (page?.loaded && !page.ids.includes(marker.id)) page.ids.push(marker.id)
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
      const gone = new Set(action.payload)
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

      for (const page of Object.values(state.canvases)) {
        page.ids = page.ids.filter((id) => !gone.has(id))
      }
    },
    /** The watch stream says a node is drawn on this canvas now, whether or not it was before. */
    canvasAdd: (state, action: PayloadAction<{ canvasId: string; ids: string[] }>) => {
      const page = state.canvases[action.payload.canvasId]
      if (!page) return

      const known = new Set(page.ids)
      for (const id of action.payload.ids) {
        if (!known.has(id)) {
          page.ids.push(id)
          known.add(id)
        }
      }
    },
    /** A node left the canvas -- it moved elsewhere, or is gone; the id watch drops what is gone. */
    canvasRemove: (state, action: PayloadAction<{ canvasId: string; ids: string[] }>) => {
      const page = state.canvases[action.payload.canvasId]
      if (!page) return

      const gone = new Set(action.payload.ids)
      page.ids = page.ids.filter((id) => !gone.has(id))
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
      .addCase(loadCanvas.fulfilled, (state, action) => {
        const { canvasId, figures } = action.payload
        for (const figure of figures) mergeFigure(state, figure)

        state.canvases[canvasId] = {
          ids: figures.map((figure) => figure.id).filter((id): id is string => !!id),
          loaded: true,
        }
      })
      .addCase(resolveCameraNode.fulfilled, (state, action) => {
        if (action.payload) mergeMarker(state, action.payload)
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

export const { dropNodes, canvasAdd, canvasRemove, clearObjectsError } = objectsSlice.actions
export default objectsSlice.reducer

/** The nodes currently known for a parent, in the order the server returned them. */
export function childrenOf(
  markers: Record<string, MapMarker>,
  childrenByParent: Record<string, ChildrenPage>,
  parentId: string,
): MapMarker[] {
  const page = childrenByParent[parentId]
  if (!page) return []

  return page.ids
    .map((id) => markers[id])
    .filter((marker): marker is MapMarker => marker !== undefined)
}

/** The nodes drawn on a canvas, in the order the canvas read returned them. */
export function canvasNodes(
  markers: Record<string, MapMarker>,
  canvases: Record<string, CanvasPage>,
  canvasId: string,
): MapMarker[] {
  const page = canvases[canvasId]
  if (!page) return []

  return page.ids
    .map((id) => markers[id])
    .filter((marker): marker is MapMarker => marker !== undefined)
}
