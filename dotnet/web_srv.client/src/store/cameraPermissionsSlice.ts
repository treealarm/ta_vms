import { type AnyAction, createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import type { ApiResponseDto, UserCameraPermissionsDto } from '../dto/dtos'
import { parseJson } from './api'

export type RealmUserDto = {
  username: string
  /** Admins bypass camera permissions entirely; null = Keycloak did not say. */
  isAdmin?: boolean | null
}

type CameraPermissionsState = {
  /** Realm users from Keycloak — this panel assigns rights, it does not create users. */
  users: RealmUserDto[]
  /** Grants keyed by username; only the users that have any are present. */
  byUser: Record<string, UserCameraPermissionsDto>
  aclEnforced: boolean
  isBusy: boolean
  status: string
}

const initialState: CameraPermissionsState = {
  users: [],
  byUser: {},
  aclEnforced: false,
  isBusy: false,
  status: 'Ready',
}

export const loadPermissionUsers = createAsyncThunk('cameraPermissions/loadUsers', async () => {
  // includeSelf: an admin editing permissions has to see their own row, unlike board sharing.
  // withRoles: so the panel can say outright that grants do not apply to administrators.
  const response = await fetch('/api/auth/users?includeSelf=true&withRoles=true')
  return parseJson<RealmUserDto[]>(response)
})

export const loadAllPermissions = createAsyncThunk('cameraPermissions/loadAll', async () => {
  const response = await fetch('/api/permissions/cameras')
  return parseJson<UserCameraPermissionsDto[]>(response)
})

export const saveUserPermissions = createAsyncThunk(
  'cameraPermissions/save',
  async (payload: UserCameraPermissionsDto) => {
    const response = await fetch(`/api/permissions/cameras/${encodeURIComponent(payload.username)}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    })
    return parseJson<ApiResponseDto>(response)
  }
)

export const loadAclMode = createAsyncThunk('cameraPermissions/loadMode', async () => {
  const response = await fetch('/api/permissions/mode')
  return parseJson<{ enforced: boolean }>(response)
})

export const setAclMode = createAsyncThunk('cameraPermissions/setMode', async (enforced: boolean) => {
  const response = await fetch('/api/permissions/mode', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ enforced }),
  })
  return parseJson<ApiResponseDto>(response)
})

const cameraPermissionsSlice = createSlice({
  name: 'cameraPermissions',
  initialState,
  reducers: {},
  extraReducers: (builder) => {
    builder
      .addCase(loadPermissionUsers.fulfilled, (state, action) => {
        state.users = action.payload
      })
      .addCase(loadAllPermissions.fulfilled, (state, action) => {
        state.byUser = Object.fromEntries(action.payload.map((u) => [u.username, u]))
        state.status = 'Ready'
      })
      .addCase(saveUserPermissions.fulfilled, (state, action) => {
        state.status = `${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
      })
      .addCase(loadAclMode.fulfilled, (state, action) => {
        state.aclEnforced = action.payload.enforced
      })
      .addCase(setAclMode.fulfilled, (state, action) => {
        state.status = `${action.payload.success ? 'OK' : 'Fail'}: ${action.payload.message}`
      })
      .addMatcher(
        (action: AnyAction) => action.type.startsWith('cameraPermissions/') && action.type.endsWith('/pending'),
        (state) => { state.isBusy = true }
      )
      .addMatcher(
        (action: AnyAction) => action.type.startsWith('cameraPermissions/') && action.type.endsWith('/fulfilled'),
        (state) => { state.isBusy = false }
      )
      .addMatcher(
        (action: AnyAction) => action.type.startsWith('cameraPermissions/') && action.type.endsWith('/rejected'),
        (state, action: AnyAction) => {
          state.isBusy = false
          state.status = `Error: ${action.error?.message ?? 'Unknown error'}`
        }
      )
  },
})

export default cameraPermissionsSlice.reducer
