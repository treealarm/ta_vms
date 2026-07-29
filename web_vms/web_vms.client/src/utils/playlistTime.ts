// The only sanctioned mapping between a <video>'s media time and wall-clock time.
//
// Why not "range start + currentTime": a playlist stitches many clips together, and the gaps
// between them (a dropped connection, a recording pause) exist in wall time but not in media time.
// Those gaps accumulate — on a long range the naive mapping drifts by minutes, which shows up as
// two cameras on the same playhead displaying visibly different moments. The server emits
// #EXT-X-PROGRAM-DATE-TIME per segment precisely so the client never has to guess.

export type PdtFragment = {
  /** Offset of this fragment inside the playlist's media timeline, seconds. */
  startMediaSec: number
  durationSec: number
  /** Wall-clock instant of the fragment's first sample. */
  wallMs: number
}

/**
 * Fragments are stored together with the playlist URL they came from. Keeping them in one object
 * makes it structurally impossible to map a freshly loaded video through the previous playlist's
 * fragment list — a mismatch that looks like a random multi-minute jump.
 */
export type PlaylistMap = {
  url: string
  fragments: PdtFragment[]
}

export function playlistWallBounds(map: PlaylistMap | null | undefined): { startMs: number; endMs: number } | null {
  if (!map?.fragments.length) return null
  const first = map.fragments[0]
  const last = map.fragments[map.fragments.length - 1]
  return { startMs: first.wallMs, endMs: last.wallMs + last.durationSec * 1000 }
}

export function fragmentAtWallMs(map: PlaylistMap | null | undefined, wallMs: number): PdtFragment | null {
  if (!map?.fragments.length) return null
  for (const f of map.fragments) {
    if (wallMs >= f.wallMs && wallMs < f.wallMs + f.durationSec * 1000) return f
    if (f.wallMs > wallMs) break
  }
  return null
}

function fragmentAtMediaTime(map: PlaylistMap, mediaSec: number): PdtFragment | null {
  for (const f of map.fragments) {
    if (mediaSec >= f.startMediaSec && mediaSec < f.startMediaSec + f.durationSec) return f
    if (f.startMediaSec > mediaSec) break
  }
  return null
}

/** Wall instant currently on screen, or null when the media time falls outside the known fragments. */
export function wallMsAtMediaTime(map: PlaylistMap | null | undefined, mediaSec: number): number | null {
  if (!map?.fragments.length || !Number.isFinite(mediaSec)) return null

  const frag = fragmentAtMediaTime(map, mediaSec)
  if (frag) return frag.wallMs + (mediaSec - frag.startMediaSec) * 1000

  // Past the end of the last fragment (the player ran slightly beyond it) — extrapolate from it
  // rather than reporting nothing, so a cell at the tail of its range still publishes a sane clock.
  const last = map.fragments[map.fragments.length - 1]
  if (mediaSec >= last.startMediaSec) return last.wallMs + (mediaSec - last.startMediaSec) * 1000

  const first = map.fragments[0]
  if (mediaSec < first.startMediaSec) return first.wallMs
  return null
}

/**
 * Media time to seek to in order to show `wallMs`.
 * Returns null when the instant is inside the playlist's wall span but lands in a recording hole —
 * the caller must pause and say "no recording at this time" rather than rebuild the playlist,
 * which would otherwise repeat every tick.
 */
export function mediaTimeAtWallMs(map: PlaylistMap | null | undefined, wallMs: number): number | null {
  if (!map?.fragments.length || !Number.isFinite(wallMs)) return null

  const frag = fragmentAtWallMs(map, wallMs)
  if (!frag) return null
  return frag.startMediaSec + (wallMs - frag.wallMs) / 1000
}

/**
 * Media time for `wallMs`, falling forward to the next fragment when the instant lands in a hole.
 *
 * Used for positioning after a seek: clicking an empty second between two recordings should open
 * the recording that follows, not refuse to move. Following logic must keep using
 * `mediaTimeAtWallMs`, where a hole has to stay distinguishable so the cell pauses instead of
 * silently jumping ahead of the wall.
 */
export function mediaTimeAtOrAfterWallMs(
  map: PlaylistMap | null | undefined,
  wallMs: number
): number | null {
  const exact = mediaTimeAtWallMs(map, wallMs)
  if (exact !== null) return exact
  if (!map?.fragments.length) return null

  const next = map.fragments.find((f) => f.wallMs >= wallMs)
  return next ? next.startMediaSec : null
}
