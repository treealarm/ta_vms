import { store } from '../store'
import { logout, setTokens } from '../store/authSlice'

/**
 * hls.js loads manifests/segments via its own XHR loader, bypassing window.fetch entirely — pass
 * this as `xhrSetup` to any `new Hls({...})` config so archive/live playback keeps working once
 * KEYCLOAK_URL is enabled.
 */
export function authXhrSetup(xhr: XMLHttpRequest): void {
  const token = store.getState().auth.token
  if (token) {
    xhr.setRequestHeader('Authorization', `Bearer ${token}`)
  }
}

let installed = false

/**
 * Attaches the stored Bearer token to same-origin /api requests and redirects to /login on 401.
 * Installed once as a global fetch wrapper instead of touching every existing fetch() call site —
 * when KEYCLOAK_URL is unset on the backend, no request ever 401s, so this is a no-op.
 */
// A single in-flight refresh shared by all concurrent 401s, so a burst of expired-token requests
// triggers exactly one /api/auth/refresh instead of a stampede. Resolves to the new access token,
// or null when refresh isn't possible (no refresh token / Keycloak rejected it).
let refreshInFlight: Promise<string | null> | null = null

export function installAuthFetch(): void {
  if (installed) return
  installed = true

  const nativeFetch = window.fetch.bind(window)

  const refreshToken = async (): Promise<string | null> => {
    const rt = store.getState().auth.refreshToken
    if (!rt) return null
    try {
      // Bypass this wrapper's own token/401 handling: /api/auth/* is anonymous and must not recurse.
      const resp = await nativeFetch('/api/auth/refresh', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken: rt }),
      })
      if (!resp.ok) return null
      const data = (await resp.json()) as { access_token: string; refresh_token: string }
      store.dispatch(setTokens({ token: data.access_token, refreshToken: data.refresh_token }))
      return data.access_token
    } catch {
      return null
    }
  }

  const withAuth = (init: RequestInit | undefined, input: RequestInfo | URL, token: string | null): RequestInit | undefined => {
    if (!token) return init
    const headers = new Headers(init?.headers ?? (typeof input === 'object' && !(input instanceof URL) ? input.headers : undefined))
    headers.set('Authorization', `Bearer ${token}`)
    return { ...init, headers }
  }

  window.fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const url = typeof input === 'string' || input instanceof URL ? input.toString() : input.url
    // /api/auth/* is anonymous-accessible and handles its own error states (e.g. bad login
    // credentials return 401 too) — never auto-redirect or attach a stale token for those.
    const isApiCall = url.startsWith('/api/') && !url.startsWith('/api/auth/')

    if (!isApiCall) return nativeFetch(input, init)

    const token = store.getState().auth.token
    let response = await nativeFetch(input, withAuth(init, input, token))

    // Access tokens are short-lived (Keycloak default ~5min). On expiry, transparently refresh once
    // using the stored refresh token and retry the request, so active users aren't bounced to login.
    if (response.status === 401) {
      if (!refreshInFlight) refreshInFlight = refreshToken().finally(() => { refreshInFlight = null })
      const newToken = await refreshInFlight

      if (newToken) {
        response = await nativeFetch(input, withAuth(init, input, newToken))
      }

      if ((!newToken || response.status === 401) && window.location.pathname !== '/login') {
        store.dispatch(logout())
        window.location.href = '/login'
      }
    }

    return response
  }
}
