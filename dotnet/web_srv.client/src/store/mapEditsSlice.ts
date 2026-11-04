import { createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import { mapApi } from '../api/mapApi'
import type {
  MapDiagramCoordinate, MapExtraProperty, MapFigure, MapObject, MapVisual,
} from '../dto/mapDtos'
import { canvasAdd, dropNodes, loadCanvas, loadNodes, loadObjProps } from './objectsSlice'

export type MapEditsState = {
  busy: number
  error: string | null
}

const initialState: MapEditsState = { busy: 0, error: null }

/**
 * Writes a shape as it stands — a new node with its geometry, or one whose vertices were just
 * dragged. The node is written first: `UpdateFigures` also writes the tree row from the figure,
 * so the row and the shape must agree on the name, the parent and the owner. `canvasId` names the
 * open canvas, whose composition takes the shape at once.
 */
export const saveFigure = createAsyncThunk(
  'mapEdits/saveFigure',
  async ({ figure, canvasId }: { figure: MapFigure; canvasId?: string }, api) => {
    await mapApi.updateObjects([{
      id: figure.id,
      parentId: figure.parentId,
      ownerId: figure.ownerId,
      name: figure.name,
    }])

    await mapApi.updateFigures([figure])
    await api.dispatch(loadNodes([figure.id]))
    if (canvasId) api.dispatch(canvasAdd({ canvasId, ids: [figure.id] }))
    return figure
  },
)

/** Moves one node to another scale layer, or off every layer: its shape is the only place that
 *  knows the level, so the figure has to travel back whole — the write also carries the row. */
export const saveZoomLevel = createAsyncThunk(
  'mapEdits/saveZoomLevel',
  async (figure: MapFigure, api) => {
    await mapApi.updateFigures([{ ...figure, extraProps: undefined }])
    await api.dispatch(loadNodes([figure.id!]))
    return figure
  },
)

export type NodeDetails = {
  /** The tree row as it is; `ownerId` has to travel back or a replica loses its owner. */
  node: MapObject
  properties: MapExtraProperty[]
  visual: MapVisual
  /** A node with a shape also carries its zoom layer; a node without one has none. */
  figure?: MapFigure
}

/** Name, properties, look and zoom layer of one node in four writes; the shape's own properties
 *  are stripped because the property list was just replaced whole. */
export const saveNodeDetails = createAsyncThunk(
  'mapEdits/saveNodeDetails',
  async ({ node, properties, visual, figure }: NodeDetails, api) => {
    await mapApi.updateObjects([node])
    await mapApi.updateProperties([{ id: node.id, properties }], true)
    await mapApi.updateVisual([visual])

    if (figure) {
      await mapApi.updateFigures([{ ...figure, extraProps: [] }])
    }

    if (node.id) {
      // The editor reopens on what was stored: the row's version moved, and the property list
      // and look have to come back from the map rather than from the draft.
      await api.dispatch(loadNodes([node.id]))
      await api.dispatch(loadObjProps(node.id))
    }
  },
)

/**
 * Moves a node elsewhere in the tree -- into a container on the same canvas, or into a space. The
 * canvas composition is re-read because the node, and everything under it, may have left this one.
 */
export const moveNode = createAsyncThunk(
  'mapEdits/moveNode',
  async ({ node, canvasId }: { node: MapObject; canvasId: string }, api) => {
    await mapApi.updateObjects([node])
    if (node.id) await api.dispatch(loadNodes([node.id]))
    await api.dispatch(loadCanvas(canvasId))
    return node
  },
)

/**
 * A camera just placed on a canvas, when the editor had a container selected: its node goes inside
 * that container. Only a new placement takes this path -- later writes on the same canvas keep the
 * parent they find, which is what makes the next drag of the camera leave it where it is.
 */
export const movePlacedCamera = createAsyncThunk(
  'mapEdits/movePlacedCamera',
  async ({ cameraId, isReference, canvasId, containerId }: {
    cameraId: string
    isReference: boolean
    canvasId: string
    containerId: string
  }, api) => {
    const loaded = await api.dispatch(loadCanvas(canvasId)).unwrap()
    const figures = loaded.figures
    const node = figures.find((f) => f.cameraId === cameraId && (isReference ? !f.ownerId : !!f.ownerId))
      ?? figures.find((f) => f.cameraId === cameraId)
    if (!node?.id || node.parentId === containerId) return null

    await mapApi.updateObjects([{ id: node.id, parentId: containerId, ownerId: node.ownerId, name: node.name }])
    await api.dispatch(loadNodes([node.id]))
    await api.dispatch(loadCanvas(canvasId))
    return node.id
  },
)

/** Removes the node and everything under it: its shapes, states, events, tracks and rights. */
export const deleteNode = createAsyncThunk(
  'mapEdits/deleteNode',
  async (id: string, api) => {
    await mapApi.deleteObjects([id])
    api.dispatch(dropNodes([id]))
  },
)

export type ReplicaRequest = {
  ownerId: string
  parentId: string
  /** Where it is dropped on the parent canvas. */
  lat: number
  lon: number
}

/** A picture of a node on another canvas: `CreateReplica` makes the node, the figure gives it the
 *  place it was dropped at — a replica without a shape draws nothing. */
export const createReplicaAt = createAsyncThunk(
  'mapEdits/createReplicaAt',
  async ({ ownerId, parentId, lat, lon }: ReplicaRequest, api) => {
    const marker = await mapApi.createReplica({ ownerId, parentId })

    await mapApi.updateFigures([{
      id: marker.id,
      parentId,
      ownerId,
      name: marker.name,
      geometry: { type: 'Point', coord: [{ lat, lon }] },
    }])

    await api.dispatch(loadNodes([marker.id]))
    api.dispatch(canvasAdd({ canvasId: parentId, ids: [marker.id] }))
    return marker
  },
)

/** Where a node stands inside a mnemoscheme: its rectangle in the parent's pixels, or the region
 *  of the parent's type it fills — never both. */
export type DiagramPlacement = {
  id: string
  parentId: string
  ownerId?: string
  name?: string
  dgrType?: string
  regionId?: string
  geometry?: MapDiagramCoordinate
  fileId?: string
}

export const saveDiagramPlacement = createAsyncThunk(
  'mapEdits/saveDiagramPlacement',
  async (placement: DiagramPlacement) => {
    await mapApi.updateObjects([{
      id: placement.id,
      parentId: placement.parentId,
      ownerId: placement.ownerId,
      name: placement.name,
    }])

    await mapApi.updateDiagrams([{
      id: placement.id,
      dgrType: placement.dgrType,
      regionId: placement.regionId,
      // The region says where the element stands; its geometry is its own canvas either way.
      geometry: placement.geometry,
      fileId: placement.fileId,
    }])

    return placement
  },
)

/** A replica of a node inside a mnemoscheme: the node is made here, its diagrams row by the
 *  caller, which is the one that knows where on the paper it was dropped. */
export const createReplicaOnDiagram = createAsyncThunk(
  'mapEdits/createReplicaOnDiagram',
  async ({ ownerId, parentId }: { ownerId: string; parentId: string }) =>
    mapApi.createReplica({ ownerId, parentId }),
)

export type NewDiagram = {
  parentId: string
  name: string
  /** The diagram's own rectangle: its canvas when it is opened. */
  geometry: MapDiagramCoordinate
  /** Where it stands on a space, when its parent is one: a diagram on a map needs a shape. */
  at?: { lat: number; lon: number }
}

/** A diagram node under a space or another diagram. Its id is made here — the map does not hand
 *  one back for an empty one, and the diagrams row is written against it at once. */
export const createDiagramNode = createAsyncThunk(
  'mapEdits/createDiagramNode',
  async ({ parentId, name, geometry, at }: NewDiagram, api) => {
    const id = crypto.randomUUID()

    await mapApi.updateObjects([{ id, parentId, name }])
    await mapApi.updateDiagrams([{ id, geometry }])

    if (at) {
      await mapApi.updateFigures([{
        id,
        parentId,
        name,
        geometry: { type: 'Point', coord: [{ lat: at.lat, lon: at.lon }] },
      }])
      api.dispatch(canvasAdd({ canvasId: parentId, ids: [id] }))
    }

    await api.dispatch(loadNodes([id]))
    return { id, name }
  },
)

const mapEditsSlice = createSlice({
  name: 'mapEdits',
  initialState,
  reducers: {
    clearMapEditsError: (state) => {
      state.error = null
    },
  },
  extraReducers: (builder) => {
    builder
      .addMatcher(
        (action) => action.type.startsWith('mapEdits/') && action.type.endsWith('/pending'),
        (state) => {
          state.busy += 1
          state.error = null
        },
      )
      .addMatcher(
        (action) => action.type.startsWith('mapEdits/') && action.type.endsWith('/fulfilled'),
        (state) => {
          state.busy = Math.max(0, state.busy - 1)
        },
      )
      .addMatcher(
        (action) => action.type.startsWith('mapEdits/') && action.type.endsWith('/rejected'),
        (state, action) => {
          state.busy = Math.max(0, state.busy - 1)
          state.error = (action as { error?: { message?: string } }).error?.message ?? 'Unknown error'
        },
      )
  },
})

export const { clearMapEditsError } = mapEditsSlice.actions
export default mapEditsSlice.reducer
