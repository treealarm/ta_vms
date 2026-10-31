import { createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import { mapApi } from '../api/mapApi'
import { requestJson } from '../api/requestJson'
import type { MapRight, MapRightKey, MapRightsMode } from '../dto/mapDtos'

/** A user of the realm, as the rights editor offers it as a subject. */
export type RealmUser = {
  username: string
  isAdmin?: boolean | null
  /** The realm roles the user holds; a right may be given to a role instead of to a name. */
  roles?: string[]
}

export type RightsState = {
  /** The node the stored rows belong to; rows of another node are not shown. */
  nodeId: string | null
  rows: MapRight[]
  /** What each node kind accepts, from the map's own dictionary. */
  actionsByKind: Record<string, string[]>
  /** The one open/enforced switch of the whole deployment. */
  mode: MapRightsMode
  users: RealmUser[]
  loading: boolean
  error: string | null
}

const initialState: RightsState = {
  nodeId: null,
  rows: [],
  actionsByKind: {},
  mode: 'RIGHTS_MODE_OPEN',
  users: [],
  loading: false,
  error: null,
}

export const loadNodeRights = createAsyncThunk('rights/loadNode', async (objectId: string) => ({
  objectId,
  rows: (await mapApi.getRights([objectId])).rights ?? [],
}))

export const loadRightActions = createAsyncThunk('rights/loadActions', async () => {
  const table = await mapApi.getRightActions()
  const byKind: Record<string, string[]> = {}
  for (const entry of table.entries ?? []) {
    if (entry.nodeKind) byKind[entry.nodeKind] = entry.actions ?? []
  }
  return byKind
})

export const loadRightSubjects = createAsyncThunk('rights/loadSubjects', async () =>
  // withRoles: right a role as well as a user, and the roles offered are the ones somebody holds.
  requestJson<RealmUser[]>('/api/auth/users?includeSelf=true&withRoles=true'))

export const loadRightsMode = createAsyncThunk('rights/loadMode', async () =>
  (await mapApi.getRightsMode()).mode ?? 'RIGHTS_MODE_OPEN')

export const saveRightsMode = createAsyncThunk('rights/saveMode', async (mode: MapRightsMode) => {
  await mapApi.setRightsMode(mode)
  return (await mapApi.getRightsMode()).mode ?? mode
})

/** A right is a row on a node; writing one is immediate, because there is nothing to batch. */
export const grantRight = createAsyncThunk('rights/grant', async (right: MapRight) => {
  await mapApi.updateRights([right])
  return { objectId: right.objectId ?? '', rows: (await mapApi.getRights([right.objectId ?? ''])).rights ?? [] }
})

export const revokeRight = createAsyncThunk('rights/revoke', async (key: MapRightKey) => {
  await mapApi.deleteRights([key])
  return { objectId: key.objectId ?? '', rows: (await mapApi.getRights([key.objectId ?? ''])).rights ?? [] }
})

const rightsSlice = createSlice({
  name: 'rights',
  initialState,
  reducers: {
    clearRightsError: (state) => {
      state.error = null
    },
  },
  extraReducers: (builder) => {
    builder
      .addCase(loadNodeRights.fulfilled, (state, action) => {
        state.nodeId = action.payload.objectId
        state.rows = action.payload.rows
      })
      .addCase(grantRight.fulfilled, (state, action) => {
        state.nodeId = action.payload.objectId
        state.rows = action.payload.rows
      })
      .addCase(revokeRight.fulfilled, (state, action) => {
        state.nodeId = action.payload.objectId
        state.rows = action.payload.rows
      })
      .addCase(loadRightActions.fulfilled, (state, action) => {
        state.actionsByKind = action.payload
      })
      .addCase(loadRightSubjects.fulfilled, (state, action) => {
        state.users = action.payload
      })
      .addCase(loadRightsMode.fulfilled, (state, action) => {
        state.mode = action.payload
      })
      .addCase(saveRightsMode.fulfilled, (state, action) => {
        state.mode = action.payload
      })
      .addMatcher(
        (action) => action.type.startsWith('rights/') && action.type.endsWith('/pending'),
        (state) => {
          state.loading = true
          state.error = null
        },
      )
      .addMatcher(
        (action) => action.type.startsWith('rights/') && action.type.endsWith('/fulfilled'),
        (state) => {
          state.loading = false
        },
      )
      .addMatcher(
        (action) => action.type.startsWith('rights/') && action.type.endsWith('/rejected'),
        (state, action) => {
          state.loading = false
          state.error = (action as { error?: { message?: string } }).error?.message ?? 'Unknown error'
        },
      )
  },
})

export const { clearRightsError } = rightsSlice.actions
export default rightsSlice.reducer
