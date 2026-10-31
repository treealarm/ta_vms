/**
 * fetch for the API endpoints. They answer errors as {"error": "..."} — the shape the gateway
 * gives gRPC failures — so the message shown to the user is the sentence the service wrote
 * rather than a JSON blob.
 */
export async function requestJson<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, init)
  const text = await response.text()
  if (!response.ok) {
    let message = text || response.statusText
    try {
      const body = JSON.parse(text) as { error?: string }
      if (body?.error) message = body.error
    } catch {
      // Not JSON: the text itself is the message.
    }
    throw new Error(message)
  }
  return (text ? JSON.parse(text) : undefined) as T
}

/** The message of whatever an API call threw. `unwrap()` rejects with a plain {message} object,
 *  not an Error, so `String(e)` alone would show "[object Object]". */
export function errorText(e: unknown): string {
  if (e instanceof Error) return e.message
  if (typeof e === 'object' && e !== null && typeof (e as { message?: unknown }).message === 'string')
    return (e as { message: string }).message
  return String(e)
}

/** A JSON-body request init, for requests that are not a plain GET. */
export const jsonRequest = (method: string, body: unknown): RequestInit => ({
  method,
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify(body),
})

export const enc = encodeURIComponent
