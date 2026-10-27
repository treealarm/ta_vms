import { store } from '../store'
import { logout, setTokens } from '../store/authSlice'
import { diagEnd, diagEvent, diagNote, diagReplayTrail, diagStart, diagToken } from './apiDiagnostics'

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
/**
 * A refresh that produced no token failed for one of two very different reasons, and the caller
 * has to be able to tell them apart: the identity provider refused the refresh token, which ends
 * the session, or nobody answered, which says nothing about it at all. Treating the second as the
 * first threw away a perfectly good refresh token every time the gateway happened to be
 * restarting.
 */
type RefreshResult = { token: string | null; retryable: boolean }

// A single in-flight refresh shared by all concurrent 401s, so a burst of expired-token requests
// triggers exactly one /api/auth/refresh instead of a stampede.
let refreshInFlight: Promise<RefreshResult> | null = null

export function installAuthFetch(): void {
  if (installed) return
  installed = true

  const nativeFetch = window.fetch.bind(window)
  diagReplayTrail()
  diagToken(store.getState().auth.token, 'at startup')

  const attemptRefresh = async (): Promise<RefreshResult> => {
    const rt = store.getState().auth.refreshToken
    if (!rt) {
      diagEvent('token refresh skipped: no refresh token stored')
      return { token: null, retryable: false }
    }
    const startedAt = performance.now()
    try {
      // Bypass this wrapper's own token/401 handling: /api/auth/* is anonymous and must not recurse.
      const resp = await nativeFetch('/api/auth/refresh', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken: rt }),
      })
      if (!resp.ok) {
        // 5xx covers both the gateway still starting (its own 503) and the dev proxy reporting it
        // unreachable (502/504). Only a 4xx is Keycloak refusing the token.
        const retryable = resp.status >= 500
        diagEvent(`token refresh ${retryable ? 'unavailable' : 'rejected'}: ${resp.status} after ${Math.round(performance.now() - startedAt)}ms`)
        return { token: null, retryable }
      }
      const data = (await resp.json()) as { access_token: string; refresh_token: string }
      store.dispatch(setTokens({ token: data.access_token, refreshToken: data.refresh_token }))
      diagToken(data.access_token, `refreshed in ${Math.round(performance.now() - startedAt)}ms`)
      return { token: data.access_token, retryable: false }
    } catch (err) {
      // Never reached the gateway — the token is untouched and worth another try.
      diagEvent(`token refresh threw after ${Math.round(performance.now() - startedAt)}ms: ${String(err)}`)
      return { token: null, retryable: true }
    }
  }

  // Until now the token was only ever renewed after something had already failed with it. That is
  // fine for a fetch this wrapper can retry, and useless for everything that cannot: hls.js loads
  // archive segments through its own XHR loader (see authXhrSetup), and an SSE stream cannot
  // re-authenticate halfway. Both simply die when the access token ages out mid-session. Renewing
  // ahead of expiry means they never see a 401; the reactive path below stays as the backstop.
  const REFRESH_MARGIN_MS = 60_000
  const REFRESH_RETRY_MS = 15_000
  let refreshTimer: number | undefined
  let scheduledFor: string | null = null

  const expiryOf = (token: string | null): number | null => {
    if (!token) return null
    try {
      // JWT segments are base64url ('-'/'_', no padding); atob only understands base64.
      const body = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/')
      const exp = (JSON.parse(atob(body)) as { exp?: number }).exp
      return exp ? exp * 1000 : null
    } catch {
      return null
    }
  }

  const scheduleProactiveRefresh = (): void => {
    const token = store.getState().auth.token
    if (token === scheduledFor) return
    scheduledFor = token

    if (refreshTimer !== undefined) {
      clearTimeout(refreshTimer)
      refreshTimer = undefined
    }

    const expiresAt = expiryOf(token)
    if (expiresAt === null) return

    const delay = Math.max(5000, expiresAt - Date.now() - REFRESH_MARGIN_MS)
    refreshTimer = window.setTimeout(() => { void runProactiveRefresh() }, delay)
  }

  const runProactiveRefresh = async (): Promise<void> => {
    // Share the same single-flight slot as the reactive path, so a request that 401s at this moment
    // joins this refresh instead of starting a second one.
    if (!refreshInFlight) refreshInFlight = attemptRefresh().finally(() => { refreshInFlight = null })
    if ((await refreshInFlight).token) return // the store change reschedules through the subscription below

    // A refresh that failed leaves the old token ticking towards expiry with nothing else about to
    // try again — the next 401 would, by which time the session is already lost. This is not a
    // hypothetical: one refresh was observed failing after 15s ("Failed to fetch", the request
    // never got a connection), and the session died at expiry because nothing retried.
    const stillAlive = expiryOf(store.getState().auth.token)
    if (stillAlive === null || stillAlive <= Date.now()) return
    refreshTimer = window.setTimeout(() => { void runProactiveRefresh() }, REFRESH_RETRY_MS)
  }

  store.subscribe(scheduleProactiveRefresh)
  scheduleProactiveRefresh()

  // A tab opened after the access token had died starts its renewal here, not at the first 401.
  // authSlice has already refused to treat that token as a session, so the app is showing a
  // neutral wait rather than pages it is about to be thrown out of; this decides which way it goes.
  if (store.getState().auth.restoring) {
    const REVIVE_RETRY_MS = 1500
    const REVIVE_RETRY_CAP_MS = 15_000
    const REVIVE_MAX_ATTEMPTS = 8

    // Keeps trying while the reason to try again is "nobody answered". A refusal ends it at once —
    // there is nothing to wait for — and so does running out of attempts, so a gateway that never
    // comes back still lands the operator on the login form rather than on a spinner forever.
    const revive = async (attempt: number): Promise<void> => {
      const { token, retryable } = await attemptRefresh()
      if (token) return
      if (!retryable || attempt >= REVIVE_MAX_ATTEMPTS) {
        store.dispatch(logout())
        return
      }
      window.setTimeout(
        () => { void revive(attempt + 1) },
        Math.min(REVIVE_RETRY_MS * 2 ** attempt, REVIVE_RETRY_CAP_MS),
      )
    }

    void revive(0)
  }

  const withAuth = (init: RequestInit | undefined, input: RequestInfo | URL, token: string | null): RequestInit | undefined => {
    if (!token) return init
    const headers = new Headers(init?.headers ?? (typeof input === 'object' && !(input instanceof URL) ? input.headers : undefined))
    headers.set('Authorization', `Bearer ${token}`)
    return { ...init, headers }
  }

  // App bootstrap issues four requests at once and retries the set up to twenty times, so a single
  // unrenewable session used to produce dozens of logout dispatches and as many assignments to
  // window.location — each one racing the navigation the previous had already started.
  let loggingOut = false

  window.fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const url = typeof input === 'string' || input instanceof URL ? input.toString() : input.url
    // Login and refresh are anonymous and handle their own error states (bad credentials return 401
    // too) — never auto-redirect or attach a stale token for those. The rest of /api/auth/* needs
    // the token like any other call: /users and /me were sent without one, and answered only
    // because the server let the whole controller through anonymously.
    const isApiCall = url.startsWith('/api/')
      && !url.startsWith('/api/auth/login') && !url.startsWith('/api/auth/refresh')

    if (!isApiCall) return nativeFetch(input, init)

    const diagId = diagStart(url)
    try {
      const token = store.getState().auth.token
      let response = await nativeFetch(input, withAuth(init, input, token))

      // Access tokens are short-lived (Keycloak default ~5min). On expiry, transparently refresh once
      // using the stored refresh token and retry the request, so active users aren't bounced to login.
      if (response.status === 401) {
        diagNote(diagId, refreshInFlight ? 'got 401, waiting on a refresh already running' : 'got 401, starting token refresh')
        if (!refreshInFlight) refreshInFlight = attemptRefresh().finally(() => { refreshInFlight = null })
        const { token: newToken, retryable } = await refreshInFlight

        if (newToken) {
          diagNote(diagId, 'retrying with the refreshed token')
          response = await nativeFetch(input, withAuth(init, input, newToken))
        }

        // A refresh nobody answered leaves the session as it was: the caller gets its 401, and the
        // next request -- or the proactive timer -- tries again. After a restart of the whole
        // installation web_srv answers seconds before Keycloak does, and logging out here sent every
        // open tab to the login page.
        const refused = newToken ? response.status === 401 : !retryable
        if (refused && !loggingOut && window.location.pathname !== '/login') {
          loggingOut = true
          diagEvent(`giving up and logging out — ${url} (last status ${response.status})`)
          store.dispatch(logout())
          window.location.href = '/login'
        }
      }

      diagEnd(diagId, response.status)
      return response
    } catch (err) {
      diagEnd(diagId, 'threw')
      throw err
    }
  }
}
