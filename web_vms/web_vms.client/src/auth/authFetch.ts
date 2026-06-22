import { store } from '../store'
import { logout } from '../store/authSlice'

let installed = false

/**
 * Attaches the stored Bearer token to same-origin /api requests and redirects to /login on 401.
 * Installed once as a global fetch wrapper instead of touching every existing fetch() call site —
 * when KEYCLOAK_URL is unset on the backend, no request ever 401s, so this is a no-op.
 */
export function installAuthFetch(): void {
  if (installed) return
  installed = true

  const nativeFetch = window.fetch.bind(window)

  window.fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const url = typeof input === 'string' || input instanceof URL ? input.toString() : input.url
    // /api/auth/* is anonymous-accessible and handles its own error states (e.g. bad login
    // credentials return 401 too) — never auto-redirect or attach a stale token for those.
    const isApiCall = url.startsWith('/api/') && !url.startsWith('/api/auth/')
    const token = store.getState().auth.token

    if (isApiCall && token) {
      const headers = new Headers(init?.headers ?? (typeof input === 'object' && !(input instanceof URL) ? input.headers : undefined))
      headers.set('Authorization', `Bearer ${token}`)
      init = { ...init, headers }
    }

    const response = await nativeFetch(input, init)

    if (isApiCall && response.status === 401 && window.location.pathname !== '/login') {
      store.dispatch(logout())
      window.location.href = '/login'
    }

    return response
  }
}
