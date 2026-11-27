/**
 * The sentence a failed response carries.
 *
 * The gateway writes gRPC failures as {"error": "..."}; a few endpoints answer {"message": "..."};
 * a body that is not JSON is its own message. Reading all three here means a caller never shows a
 * JSON blob to the operator, and there is no second format to keep in step.
 */
export async function responseErrorText(response: Response): Promise<string> {
  const text = await response.text()
  if (!text) return response.statusText || 'The request failed'
  try {
    const body = JSON.parse(text) as { error?: unknown; message?: unknown }
    if (typeof body?.error === 'string' && body.error) return body.error
    if (typeof body?.message === 'string' && body.message) return body.message
  } catch {
    // Not JSON: the text itself is the message.
  }
  return text
}

/** fetch for the API endpoints; a failed response becomes an Error carrying its sentence. */
export async function requestJson<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, init)
  if (!response.ok) throw new Error(await responseErrorText(response))
  const text = await response.text()
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
