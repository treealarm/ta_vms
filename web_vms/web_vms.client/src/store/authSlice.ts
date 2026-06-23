import { createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import { getErrorMessage } from './api'

const TOKEN_KEY = 'vms_auth_token'
const REFRESH_TOKEN_KEY = 'vms_auth_refresh_token'

interface AuthState {
  token: string | null
  refreshToken: string | null
  loading: boolean
}

const initialState: AuthState = {
  token: localStorage.getItem(TOKEN_KEY),
  refreshToken: localStorage.getItem(REFRESH_TOKEN_KEY),
  loading: false,
}

/** Decodes the JWT's Keycloak realm roles (realm_access.roles) client-side, without verifying the signature. */
export function decodeRoles(token: string | null): string[] {
  if (!token) return []
  try {
    // JWT segments are base64url (uses '-'/'_', no padding) — atob() only understands base64.
    const base64 = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/')
    const payload = JSON.parse(atob(base64)) as { realm_access?: { roles?: string[] } }
    return payload.realm_access?.roles ?? []
  } catch {
    return []
  }
}

export const login = createAsyncThunk(
  'auth/login',
  async ({ username, password }: { username: string; password: string }) => {
    const response = await fetch('/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username, password }),
    })
    if (!response.ok) {
      throw new Error(await getErrorMessage(response))
    }
    const data = await response.json() as { access_token: string; refresh_token: string }
    return { token: data.access_token, refreshToken: data.refresh_token }
  }
)

const authSlice = createSlice({
  name: 'auth',
  initialState,
  reducers: {
    logout(state) {
      state.token = null
      state.refreshToken = null
      localStorage.removeItem(TOKEN_KEY)
      localStorage.removeItem(REFRESH_TOKEN_KEY)
    },
  },
  extraReducers: (builder) => {
    builder
      .addCase(login.pending, (state) => {
        state.loading = true
      })
      .addCase(login.fulfilled, (state, action) => {
        state.loading = false
        state.token = action.payload.token
        state.refreshToken = action.payload.refreshToken
        localStorage.setItem(TOKEN_KEY, action.payload.token)
        localStorage.setItem(REFRESH_TOKEN_KEY, action.payload.refreshToken)
      })
      .addCase(login.rejected, (state) => {
        state.loading = false
      })
  },
})

export const { logout } = authSlice.actions
export default authSlice.reducer
