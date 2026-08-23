/**
 * Development diagnostics for the /api choke point.
 *
 * Every API call in the app goes through the wrapper installed by `installAuthFetch`, which makes
 * that wrapper the only place able to see a request that was issued and never came back. That is
 * the shape of the "the page stopped updating" failure: the backend answers a fresh curl in
 * milliseconds, so what is stuck is a promise in this tab, not a handler on the server.
 *
 * Silence with `localStorage.setItem('vms.apiDiag', 'off')`, re-enable by removing the key.
 */

const STALL_MS = 5000

// Server-Sent Events streams stay open for as long as the overlay is on, so they are "in flight"
// forever and would head every stall report — which they did, hiding the requests that were
// actually stuck. They still count against the browser's six-per-origin budget, so the sweep says
// how many are open rather than pretending they are not there.
const isStream = (url: string) => url.includes('/api/analytics/live')

type Entry = { id: number; url: string; startedAt: number; note: string }

const live = new Map<number, Entry>()
let nextId = 1
let sweeper: number | undefined
let tokenExpiresAtMs = 0

function on(): boolean {
  try {
    return import.meta.env.DEV && localStorage.getItem('vms.apiDiag') !== 'off'
  } catch {
    return import.meta.env.DEV
  }
}

const secsToExpiry = () =>
  tokenExpiresAtMs === 0 ? '?' : Math.round((tokenExpiresAtMs - Date.now()) / 1000).toString()

const TRAIL_KEY = 'vms.apiDiag.trail'
const TRAIL_MAX = 40

/**
 * The interesting failure ends in `window.location.href = '/login'`, which wipes the console
 * unless the reader thought to tick "Preserve log" beforehand — and nobody does, because the
 * reload is the surprise. So the lines that explain it are carried across the reload in
 * sessionStorage (which survives a same-tab navigation, unlike the tokens `logout` clears) and
 * replayed on the next startup.
 */
function trailPush(line: string): void {
  if (!on()) return
  try {
    const kept = JSON.parse(sessionStorage.getItem(TRAIL_KEY) ?? '[]') as string[]
    kept.push(`${new Date().toISOString().slice(11, 23)} ${line}`)
    sessionStorage.setItem(TRAIL_KEY, JSON.stringify(kept.slice(-TRAIL_MAX)))
  } catch {
    // storage disabled or full — the live console still has the same line
  }
}

/** Prints, once per document, what the previous one was doing before it reloaded. */
export function diagReplayTrail(): void {
  if (!on()) return
  let kept: string[] = []
  try {
    kept = JSON.parse(sessionStorage.getItem(TRAIL_KEY) ?? '[]') as string[]
    sessionStorage.removeItem(TRAIL_KEY)
  } catch {
    return
  }
  if (kept.length === 0) return
  console.warn(`[api] --- ${kept.length} line(s) from before the last reload ---`)
  for (const l of kept) console.warn(`[api] | ${l}`)
  console.warn('[api] --- end of carried-over log ---')
}

/** Records when the current access token dies, so a stall can be read against it. */
export function diagToken(token: string | null, label: string): void {
  if (!on()) return
  tokenExpiresAtMs = 0
  if (token) {
    try {
      const body = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/')
      const exp = (JSON.parse(atob(body)) as { exp?: number }).exp
      if (exp) tokenExpiresAtMs = exp * 1000
    } catch {
      // not a JWT we can read — leave the expiry unknown rather than guessing
    }
  }
  const line = `token ${label}: expires in ${secsToExpiry()}s`
  console.log(`[api] ${line}`)
  trailPush(line)
}

export function diagStart(url: string): number {
  if (!on()) return 0
  const id = nextId
  nextId += 1
  live.set(id, { id, url, startedAt: performance.now(), note: '' })
  if (sweeper === undefined) sweeper = window.setInterval(sweep, STALL_MS)
  return id
}

export function diagNote(id: number, note: string): void {
  const e = live.get(id)
  if (!e) return
  e.note = note
  console.warn(`[api] #${id} ${note} — ${e.url}`)
  trailPush(`#${id} ${note} — ${e.url}`)
}

export function diagEnd(id: number, outcome: number | string): void {
  const e = live.get(id)
  if (!e) return
  live.delete(id)
  const ms = Math.round(performance.now() - e.startedAt)
  if (ms >= STALL_MS) console.warn(`[api] #${id} finished ${outcome} after ${ms}ms — ${e.url}`)
}

/**
 * The signal that matters. One slow request is noise; several stuck at once — especially all of
 * them waiting on the same token refresh — is the freeze, and it names what to look at.
 */
function sweep(): void {
  const now = performance.now()
  const open = [...live.values()]
  const streams = open.filter((e) => isStream(e.url)).length
  const stalled = open.filter((e) => now - e.startedAt > STALL_MS && !isStream(e.url))
  if (stalled.length === 0) return

  console.warn(
    `[api] ${stalled.length} of ${open.length} request(s) in flight over ${STALL_MS / 1000}s `
    + `(${streams} long-lived stream(s) excluded, token expires in ${secsToExpiry()}s):`
  )
  for (const e of stalled) {
    console.warn(`[api]   ${Math.round((now - e.startedAt) / 1000)}s ${e.note || 'no response yet'} — ${e.url}`)
  }
}

/** For the caller's own milestones (refresh outcome, forced logout) — logged and carried over. */
export function diagEvent(line: string): void {
  if (!on()) return
  console.warn(`[api] ${line}`)
  trailPush(line)
}
