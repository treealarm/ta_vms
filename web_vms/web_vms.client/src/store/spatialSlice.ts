import { type AnyAction, createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import type {
  CameraPlacementDto,
  LandmarkDto,
  PlanCornersDto,
  SpaceDto,
} from '../dto/dtos'

type SpatialState = {
  spaces: SpaceDto[]
  placements: CameraPlacementDto[]
  /** Landmarks are loaded per space, and only by administrators. */
  landmarksBySpace: Record<string, LandmarkDto[]>
  loaded: boolean
  /** Last failure, for the panel to show; cleared by the next success. */
  error: string | null
}

const initialState: SpatialState = {
  spaces: [],
  placements: [],
  landmarksBySpace: {},
  loaded: false,
  error: null,
}

/**
 * fetch for the maps endpoints. They answer errors as {"error": "..."}, the same shape the
 * gateway gives gRPC failures, so the message the administrator sees is the sentence VmsCfg wrote
 * rather than a JSON blob.
 */
export async function spatialRequest<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, init)
  const text = await response.text()
  if (!response.ok) {
    let message = text || response.statusText
    try {
      const body = JSON.parse(text) as { error?: string }
      if (body?.error) message = body.error
    } catch {
      // Not JSON: the text itself is the message.
    }
    throw new Error(message)
  }
  return (text ? JSON.parse(text) : undefined) as T
}

/**
 * The message of whatever a maps call threw. `unwrap()` rejects with a plain {message} object, not
 * an Error, so `String(e)` alone would show the administrator "[object Object]".
 */
export function errorText(e: unknown): string {
  if (e instanceof Error) return e.message
  if (typeof e === 'object' && e !== null && typeof (e as { message?: unknown }).message === 'string')
    return (e as { message: string }).message
  return String(e)
}

const json = (method: string, body: unknown): RequestInit => ({
  method,
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(body),
})

const enc = encodeURIComponent

export const loadSpaces = createAsyncThunk('spatial/loadSpaces', () =>
  spatialRequest<SpaceDto[]>('/api/spatial/spaces'))

export const upsertSpace = createAsyncThunk('spatial/upsertSpace', async (dto: SpaceDto, { dispatch }) => {
  const space = await spatialRequest<SpaceDto>('/api/spatial/spaces', json('PUT', dto))
  await dispatch(loadSpaces())
  return space
})

export const removeSpace = createAsyncThunk('spatial/removeSpace', async (spaceId: string, { dispatch }) => {
  await spatialRequest(`/api/spatial/spaces/${enc(spaceId)}`, { method: 'DELETE' })
  await Promise.all([dispatch(loadSpaces()), dispatch(loadPlacements())])
})

export type PlanUpload = {
  spaceId: string
  file: Blob
  widthPx: number
  heightPx: number
  corners: PlanCornersDto
}

export const uploadSpacePlan = createAsyncThunk('spatial/uploadSpacePlan', async (u: PlanUpload, { dispatch }) => {
  const form = new FormData()
  form.append('file', u.file)
  form.append('widthPx', String(u.widthPx))
  form.append('heightPx', String(u.heightPx))
  form.append('tlLat', String(u.corners.topLeft.lat))
  form.append('tlLon', String(u.corners.topLeft.lon))
  form.append('trLat', String(u.corners.topRight.lat))
  form.append('trLon', String(u.corners.topRight.lon))
  form.append('blLat', String(u.corners.bottomLeft.lat))
  form.append('blLon', String(u.corners.bottomLeft.lon))
  const space = await spatialRequest<SpaceDto>(`/api/spatial/spaces/${enc(u.spaceId)}/plan`, { method: 'POST', body: form })
  await dispatch(loadSpaces())
  return space
})

export const setPlanCorners = createAsyncThunk(
  'spatial/setPlanCorners',
  async ({ spaceId, corners }: { spaceId: string; corners: PlanCornersDto }, { dispatch }) => {
    const space = await spatialRequest<SpaceDto>(`/api/spatial/spaces/${enc(spaceId)}/plan/corners`, json('PUT', corners))
    await dispatch(loadSpaces())
    return space
  },
)

export const removeSpacePlan = createAsyncThunk('spatial/removeSpacePlan', async (spaceId: string, { dispatch }) => {
  await spatialRequest(`/api/spatial/spaces/${enc(spaceId)}/plan`, { method: 'DELETE' })
  await dispatch(loadSpaces())
})

export const loadPlacements = createAsyncThunk('spatial/loadPlacements', () =>
  spatialRequest<CameraPlacementDto[]>('/api/spatial/placements'))

export const upsertPlacement = createAsyncThunk(
  'spatial/upsertPlacement',
  async (dto: CameraPlacementDto, { dispatch }) => {
    await spatialRequest(`/api/spatial/placements/${enc(dto.cameraId)}/${enc(dto.spaceId)}`, json('PUT', dto))
    await dispatch(loadPlacements())
  },
)

export const removePlacement = createAsyncThunk(
  'spatial/removePlacement',
  async ({ cameraId, spaceId }: { cameraId: string; spaceId: string }, { dispatch }) => {
    await spatialRequest(`/api/spatial/placements/${enc(cameraId)}/${enc(spaceId)}`, { method: 'DELETE' })
    await dispatch(loadPlacements())
  },
)

export const loadLandmarks = createAsyncThunk('spatial/loadLandmarks', async (spaceId: string) => ({
  spaceId,
  landmarks: await spatialRequest<LandmarkDto[]>(`/api/spatial/spaces/${enc(spaceId)}/landmarks`),
}))

export const upsertLandmark = createAsyncThunk('spatial/upsertLandmark', async (landmark: LandmarkDto, { dispatch }) => {
  const saved = await spatialRequest<LandmarkDto>('/api/spatial/landmarks', json('PUT', landmark))
  await dispatch(loadLandmarks(landmark.spaceId))
  return saved
})

export const removeLandmark = createAsyncThunk(
  'spatial/removeLandmark',
  async ({ spaceId, landmarkId }: { spaceId: string; landmarkId: string }, { dispatch }) => {
    await spatialRequest(`/api/spatial/landmarks/${enc(landmarkId)}`, { method: 'DELETE' })
    await dispatch(loadLandmarks(spaceId))
  },
)

const spatialSlice = createSlice({
  name: 'spatial',
  initialState,
  reducers: {
    clearSpatialError: (state) => { state.error = null },
  },
  extraReducers: (builder) => {
    builder
      .addCase(loadSpaces.fulfilled, (state, action) => {
        state.spaces = action.payload
        state.loaded = true
      })
      .addCase(loadPlacements.fulfilled, (state, action) => {
        state.placements = action.payload
      })
      .addCase(loadLandmarks.fulfilled, (state, action) => {
        state.landmarksBySpace[action.payload.spaceId] = action.payload.landmarks
      })
      .addMatcher(
        (action: AnyAction) => action.type.startsWith('spatial/') && action.type.endsWith('/fulfilled'),
        (state) => { state.error = null },
      )
      .addMatcher(
        (action: AnyAction) => action.type.startsWith('spatial/') && action.type.endsWith('/rejected'),
        (state, action: AnyAction) => { state.error = action.error?.message ?? 'Unknown error' },
      )
  },
})

export const { clearSpatialError } = spatialSlice.actions
export default spatialSlice.reducer
