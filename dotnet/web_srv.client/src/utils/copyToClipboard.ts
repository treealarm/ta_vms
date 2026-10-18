/**
 * Copy `text`, answering whether it worked instead of throwing.
 *
 * The clipboard is one of the few browser APIs that refuses outright: the permission can be
 * denied, and outside a secure context `navigator.clipboard` is not there at all. Both arrive as
 * a rejected promise, and every call site is a fire-and-forget click handler, so nothing observes
 * the rejection -- which the browser then reports as an uncaught error. Returning false keeps the
 * failure where it can be acted on: the "copied" flash simply does not happen.
 */
export async function copyToClipboard(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text)
    return true
  } catch {
    return false
  }
}
