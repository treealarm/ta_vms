import { responseErrorText } from './requestJson'

/**
 * fetch for the ONVIF/PTZ endpoints; a failed response becomes an Error carrying its sentence —
 * the same reader as requestJson, so a {"error": "..."} body is never shown as JSON.
 */
export async function apiFetch<T>(url: string, init?: RequestInit): Promise<T> {
  const r = await fetch(url, init)
  if (!r.ok) throw new Error(await responseErrorText(r))
  // Several endpoints (set-config, PTZ goto, sync-time, ...) return 200 with no body —
  // r.json() throws "Unexpected end of JSON input" on those, even though the call succeeded.
  const text = await r.text()
  return (text ? JSON.parse(text) : undefined) as T
}
