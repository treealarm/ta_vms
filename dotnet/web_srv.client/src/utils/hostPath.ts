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
