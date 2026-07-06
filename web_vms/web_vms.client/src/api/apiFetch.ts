export async function apiFetch<T>(url: string, init?: RequestInit): Promise<T> {
  const r = await fetch(url, init)
  if (!r.ok) throw new Error(await r.text())
  // Several endpoints (set-config, PTZ goto, sync-time, ...) return 200 with no body —
  // r.json() throws "Unexpected end of JSON input" on those, even though the call succeeded.
  const text = await r.text()
  return (text ? JSON.parse(text) : undefined) as T
}
