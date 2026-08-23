import { createAsyncThunk, createSlice } from '@reduxjs/toolkit'
import { getErrorMessage } from './api'

const TOKEN_KEY = 'vms_auth_token'
const REFRESH_TOKEN_KEY = 'vms_auth_refresh_token'

interface AuthState {
  token: string | null
  refreshToken: string | null
  loading: boolean
  /** A stored session is being revived; nothing should be drawn until this settles either way. */
  restoring: boolean
  /** A session existed and is over. The app must show login rather than its own pages. */
  expired: boolean
}

/** Milliseconds until the JWT expires, or null when it carries no readable expiry. */
function msUntilExpiry(token: string | null): number | null {
  if (!token) return null
  try {
    // JWT segments are base64url ('-'/'_', no padding); atob only understands base64.
    const payload = JSON.parse(atob(token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/'))) as { exp?: number }
    return payload.exp ? payload.exp * 1000 - Date.now() : null
  } catch {
    return null
  }
}

// "Logged in" used to mean nothing more than "there is a string under this key", so a tab opened
// after the token had died rendered the whole app, collected a round of 401s and only then threw
// the operator out to login. Deciding it here, before the first render, is what removes that.
//
// An expiry that cannot be read is treated as usable: the token may not be a JWT at all in some
// deployment, and refusing to start on that guess would be worse than the 401 it would have cost.
const storedToken = localStorage.getItem(TOKEN_KEY)
const storedRefreshToken = localStorage.getItem(REFRESH_TOKEN_KEY)
const remaining = msUntilExpiry(storedToken)
const tokenIsDead = storedToken !== null && remaining !== null && remaining <= 0

// Dead with a refresh token beside it may still be recoverable, and installAuthFetch tries exactly
// that at startup; dead with nothing to recover from is simply the end of the session.
if (tokenIsDead && storedRefreshToken === null) localStorage.removeItem(TOKEN_KEY)

const initialState: AuthState = {
  token: tokenIsDead ? null : storedToken,
  refreshToken: storedRefreshToken,
  loading: false,
  restoring: tokenIsDead && storedRefreshToken !== null,
  expired: tokenIsDead && storedRefreshToken === null,
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
      state.restoring = false
      // Both the explicit "log out" and giving up on a revival end here, and both want login shown.
      state.expired = true
      localStorage.removeItem(TOKEN_KEY)
      localStorage.removeItem(REFRESH_TOKEN_KEY)
    },
    // Applied after a silent token refresh (see installAuthFetch) — Keycloak rotates the refresh
    // token too, so both are replaced.
    setTokens(state, action: { payload: { token: string; refreshToken: string } }) {
      state.token = action.payload.token
      state.refreshToken = action.payload.refreshToken
      state.restoring = false
      state.expired = false
      localStorage.setItem(TOKEN_KEY, action.payload.token)
      localStorage.setItem(REFRESH_TOKEN_KEY, action.payload.refreshToken)
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
        state.restoring = false
        state.expired = false
        localStorage.setItem(TOKEN_KEY, action.payload.token)
        localStorage.setItem(REFRESH_TOKEN_KEY, action.payload.refreshToken)
      })
      .addCase(login.rejected, (state) => {
        state.loading = false
      })
  },
})

export const { logout, setTokens } = authSlice.actions
export default authSlice.reducer
