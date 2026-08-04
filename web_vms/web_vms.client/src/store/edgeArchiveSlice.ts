import { type AnyAction, type PayloadAction, createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import type {
  EdgeCapabilitiesDto,
  EdgeExportDto,
  EdgeImportJobDto,
  EdgeImportRequestDto,
  EdgeIntervalsDto,
  EdgeRecordingJobsDto,
  EdgeRecordingsDto,
  EdgeStorageDto,
  EdgeSummaryDto
} from '../dto/dtos'
import { parseJson } from './api'

type EdgeArchiveState = {
  /** Camera the stored data belongs to; responses for any other camera are dropped. */
  cameraId: string | null
  capabilities: EdgeCapabilitiesDto | null
  summary: EdgeSummaryDto | null
  recordings: EdgeRecordingsDto | null
  intervals: EdgeIntervalsDto | null
  recordingJobs: EdgeRecordingJobsDto | null
  storage: EdgeStorageDto | null
  jobs: EdgeImportJobDto[]
  exports: EdgeExportDto[]
  isBusy: boolean
  /** Set to the search/import error so the panel can surface it; cleared on the next success. */
  status: string
}

const initialState: EdgeArchiveState = {
  cameraId: null,
  capabilities: null,
  summary: null,
  recordings: null,
  intervals: null,
  recordingJobs: null,
  storage: null,
  jobs: [],
  exports: [],
  isBusy: false,
  status: ''
}

const base = '/api/edge-archive/cameras'

export const fetchEdgeCapabilities = createAsyncThunk(
  'edgeArchive/fetchCapabilities',
  async (cameraId: string) =>
    parseJson<EdgeCapabilitiesDto>(await fetch(`${base}/${encodeURIComponent(cameraId)}/capabilities`))
)

export const fetchEdgeSummary = createAsyncThunk(
  'edgeArchive/fetchSummary',
  async (cameraId: string) =>
    parseJson<EdgeSummaryDto>(await fetch(`${base}/${encodeURIComponent(cameraId)}/summary`))
)

export const fetchEdgeRecordings = createAsyncThunk(
  'edgeArchive/fetchRecordings',
  async (cameraId: string) =>
    parseJson<EdgeRecordingsDto>(await fetch(`${base}/${encodeURIComponent(cameraId)}/recordings`))
)

export const fetchEdgeIntervals = createAsyncThunk(
  'edgeArchive/fetchIntervals',
  async (args: { cameraId: string; from?: string; to?: string; recordingToken?: string; maxResults?: number }) => {
    const params = new URLSearchParams()
    if (args.from) params.set('from', args.from)
    if (args.to) params.set('to', args.to)
    if (args.recordingToken) params.set('recordingToken', args.recordingToken)
    if (args.maxResults) params.set('maxResults', String(args.maxResults))
    const query = params.toString()
    return parseJson<EdgeIntervalsDto>(
      await fetch(`${base}/${encodeURIComponent(args.cameraId)}/intervals${query ? `?${query}` : ''}`)
    )
  }
)

export const fetchEdgeRecordingJobs = createAsyncThunk(
  'edgeArchive/fetchRecordingJobs',
  async (cameraId: string) =>
    parseJson<EdgeRecordingJobsDto>(await fetch(`${base}/${encodeURIComponent(cameraId)}/recording-jobs`))
)

export const setEdgeRecordingJobMode = createAsyncThunk(
  'edgeArchive/setRecordingJobMode',
  async (args: { cameraId: string; jobToken: string; mode: string }, { dispatch }) => {
    const response = await fetch(
      `${base}/${encodeURIComponent(args.cameraId)}/recording-jobs/${encodeURIComponent(args.jobToken)}/mode`,
      { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ mode: args.mode }) }
    )
    await parseJson<unknown>(response)
    await dispatch(fetchEdgeRecordingJobs(args.cameraId))
  }
)

export const deleteEdgeRecording = createAsyncThunk(
  'edgeArchive/deleteRecording',
  async (args: { cameraId: string; recordingToken: string }, { dispatch }) => {
    const response = await fetch(
      `${base}/${encodeURIComponent(args.cameraId)}/recordings/${encodeURIComponent(args.recordingToken)}`,
      { method: 'DELETE' }
    )
    await parseJson<unknown>(response)
    await dispatch(fetchEdgeRecordings(args.cameraId))
    await dispatch(fetchEdgeRecordingJobs(args.cameraId))
  }
)

export const fetchEdgeStorage = createAsyncThunk(
  'edgeArchive/fetchStorage',
  async (cameraId: string) =>
    parseJson<EdgeStorageDto>(await fetch(`${base}/${encodeURIComponent(cameraId)}/storage`))
)

export const formatEdgeStorage = createAsyncThunk(
  'edgeArchive/formatStorage',
  async (args: { cameraId: string; command: string }, { dispatch }) => {
    const response = await fetch(`${base}/${encodeURIComponent(args.cameraId)}/storage/format`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ command: args.command })
    })
    await parseJson<unknown>(response)
    await dispatch(fetchEdgeStorage(args.cameraId))
  }
)

export const fetchEdgeImports = createAsyncThunk(
  'edgeArchive/fetchImports',
  async (cameraId: string) =>
    parseJson<EdgeImportJobDto[]>(await fetch(`${base}/${encodeURIComponent(cameraId)}/imports`))
)

export const startEdgeImport = createAsyncThunk(
  'edgeArchive/startImport',
  async (args: { cameraId: string; request: EdgeImportRequestDto }) => {
    const response = await fetch(`${base}/${encodeURIComponent(args.cameraId)}/imports`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(args.request)
    })
    return parseJson<EdgeImportJobDto>(response)
  }
)

export const cancelEdgeImport = createAsyncThunk(
  'edgeArchive/cancelImport',
  async (args: { cameraId: string; jobId: string }, { dispatch }) => {
    const response = await fetch(
      `${base}/${encodeURIComponent(args.cameraId)}/imports/${encodeURIComponent(args.jobId)}`,
      { method: 'DELETE' }
    )
    await parseJson<unknown>(response)
    await dispatch(fetchEdgeImports(args.cameraId))
  }
)

export const fetchEdgeExports = createAsyncThunk(
  'edgeArchive/fetchExports',
  async (cameraId: string) =>
    parseJson<EdgeExportDto[]>(await fetch(`${base}/${encodeURIComponent(cameraId)}/exports`))
)

/** Camera a thunk was dispatched for: either the bare argument or the cameraId of its argument object. */
function argCameraId(arg: unknown): string | null {
  if (typeof arg === 'string') return arg
  if (arg && typeof arg === 'object' && 'cameraId' in arg) {
    const value = (arg as { cameraId?: unknown }).cameraId
    return typeof value === 'string' ? value : null
  }
  return null
}

function isCurrent(state: EdgeArchiveState, action: { meta: { arg: unknown } }): boolean {
  return argCameraId(action.meta.arg) === state.cameraId
}

const edgeArchiveSlice = createSlice({
  name: 'edgeArchive',
  initialState,
  reducers: {
    // Selecting another camera must not leave the previous one's data on screen, and must also
    // invalidate requests already in flight — a slow answer for the old camera would otherwise land
    // on top of the new one's state.
    selectEdgeCamera: (state, action: PayloadAction<string | null>) => {
      state.cameraId = action.payload
      state.capabilities = null
      state.summary = null
      state.recordings = null
      state.intervals = null
      state.recordingJobs = null
      state.storage = null
      state.jobs = []
      state.exports = []
      state.status = ''
    }
  },
  extraReducers: (builder) => {
    builder
      .addCase(fetchEdgeCapabilities.fulfilled, (state, action) => {
        if (isCurrent(state, action)) state.capabilities = action.payload
      })
      .addCase(fetchEdgeSummary.fulfilled, (state, action) => {
        if (isCurrent(state, action)) state.summary = action.payload
      })
      .addCase(fetchEdgeRecordings.fulfilled, (state, action) => {
        if (isCurrent(state, action)) state.recordings = action.payload
      })
      .addCase(fetchEdgeIntervals.fulfilled, (state, action) => {
        if (isCurrent(state, action)) state.intervals = action.payload
      })
      .addCase(fetchEdgeRecordingJobs.fulfilled, (state, action) => {
        if (isCurrent(state, action)) state.recordingJobs = action.payload
      })
      .addCase(fetchEdgeStorage.fulfilled, (state, action) => {
        if (isCurrent(state, action)) state.storage = action.payload
      })
      .addCase(fetchEdgeImports.fulfilled, (state, action) => {
        if (isCurrent(state, action)) state.jobs = action.payload
      })
      .addCase(fetchEdgeExports.fulfilled, (state, action) => {
        if (isCurrent(state, action)) state.exports = action.payload
      })
      .addMatcher((a: AnyAction) => a.type.startsWith('edgeArchive/') && a.type.endsWith('/pending'), (state) => {
        state.isBusy = true
      })
      .addMatcher((a: AnyAction) => a.type.startsWith('edgeArchive/') && a.type.endsWith('/fulfilled'), (state) => {
        state.isBusy = false
        state.status = ''
      })
      .addMatcher((a: AnyAction) => a.type.startsWith('edgeArchive/') && a.type.endsWith('/rejected'), (state, action: AnyAction) => {
        state.isBusy = false
        state.status = action.error?.message ?? 'Unknown error'
      })
  }
})

export const { selectEdgeCamera } = edgeArchiveSlice.actions
export default edgeArchiveSlice.reducer
