/** Fallback used when the camera has no media router assigned, so we don't know what STUN
 *  (if any) the deployment wants. Matches the value this client hardcoded before STUN became
 *  configurable per media router. */
export const DEFAULT_STUN_URL = 'stun:stun.l.google.com:19302'

/**
 * Builds the ICE config for WHEP playback from a media router's STUN URL.
 *
 * - undefined/null (no router assigned) → the public default, preserving the old behavior
 * - empty string                        → no STUN at all: host candidates only, LAN-only viewing
 * - anything else                       → that STUN/TURN URL
 *
 * On a LAN the empty case is the fast path: host candidates are ready immediately, so ICE never
 * waits on an unreachable or slow public server.
 */
export function iceServersFromStunUrl(stunUrl: string | null | undefined): RTCIceServer[] {
  if (stunUrl === undefined || stunUrl === null) return [{ urls: DEFAULT_STUN_URL }]
  const trimmed = stunUrl.trim()
  return trimmed ? [{ urls: trimmed }] : []
}
