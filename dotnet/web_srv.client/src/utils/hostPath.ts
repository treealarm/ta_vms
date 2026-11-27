/**
 * Absolute path of a recording node, in either OS style.
 *
 * The node that records may run Windows while the services validating the value run on Linux, so
 * both styles are accepted and neither is ever rewritten into the other. Mirrors `HostPath` in
 * web_srv.Types, which is what the gateway enforces — this copy only spares the operator a round
 * trip. Existence and writability are answered by the node itself, through the browse dialog.
 */
export function isAbsoluteHostPath(path: string): boolean {
  const value = path.trim()
  if (value === '') return false

  return value.startsWith('/')
    || value.startsWith('\\\\')
    || /^[A-Za-z]:[\\/]/.test(value)
}

/**
 * The same path as a person reads it: `.` dropped and `..` collapsed, without asking the file
 * system. Display only — the stored value is whatever the node was configured with, and rewriting
 * it would change what a deployment records.
 *
 * Both OS styles are understood and the separator the caller used is kept. A drive letter and a UNC
 * share are roots: `..` never pops above them.
 */
export function normalizeDisplayPath(path: string): string {
  const value = path.trim()
  if (value === '') return ''

  // Mixed separators are not produced by either OS; pick the only style present, else POSIX.
  const sep = value.includes('\\') && !value.includes('/') ? '\\' : '/'
  const unc = /^[\\/]{2}/.test(value)
  const drive = /^[A-Za-z]:/.exec(value)?.[0] ?? ''
  const rooted = !drive && !unc && /^[\\/]/.test(value)

  let rest = value
  if (drive) rest = value.slice(drive.length)
  else if (unc) rest = value.slice(2)
  else if (rooted) rest = value.slice(1)

  const parts = rest.split(/[\\/]/).filter(Boolean)
  // The server and share of a UNC path are the root, like `/` for a POSIX one.
  const pinned = unc ? Math.min(2, parts.length) : 0
  const out: string[] = []

  for (const part of parts) {
    if (part === '.') continue
    if (part === '..') {
      if (out.length > pinned) out.pop()
      continue
    }
    out.push(part)
  }

  const body = out.join(sep)
  if (drive) return body ? `${drive}${sep}${body}` : `${drive}${sep}`
  if (unc) return `${sep}${sep}${body}`
  if (rooted) return `/${body}`
  return body || value
}
