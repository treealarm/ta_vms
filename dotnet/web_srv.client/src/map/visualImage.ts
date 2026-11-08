/**
 * The picture of a node: `visual.image` is bytes and nothing says what they are. Everything the map
 * handed out used to be a PNG, and an SVG icon — a diagram's background used as an icon, say — was
 * painted as a broken picture; the first bytes say which it is, so the browser is told the truth.
 * A value that already is a data URL is passed through: the editor stores what it was given.
 */
export function visualImageUrl(base64: string | null | undefined): string {
  if (!base64) return ''
  if (base64.startsWith('data:')) return base64

  const svg = startsWithMarkup(base64)
  return `data:${svg ? 'image/svg+xml' : 'image/png'};base64,${base64}`
}

/** Whether the bytes behind a base64 string open like markup — `<svg`, or an XML prolog. */
function startsWithMarkup(base64: string): boolean {
  try {
    // Enough to tell: the first 48 bytes, cut on a base64 boundary.
    return atob(base64.slice(0, 64)).trimStart().startsWith('<')
  } catch {
    return false
  }
}
