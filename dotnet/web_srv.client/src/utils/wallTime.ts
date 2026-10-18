// Wall-clock helpers for archive playback. Everything here is epoch milliseconds — the timeline,
// the shared playhead and the HLS PDT mapping all speak the same unit, so there is no ISO/Date
// juggling in the hot path.
//
// "Local day" means the operator's day, not UTC: day navigation must land on the midnight the
// operator sees on the wall clock behind them.

export type WallRange = { startMs: number; endMs: number }

export const SECOND_MS = 1000
export const MINUTE_MS = 60 * SECOND_MS
export const HOUR_MS = 60 * MINUTE_MS
export const DAY_MS = 24 * HOUR_MS

export function clampMs(value: number, lo: number, hi: number): number {
  if (hi < lo) return lo
  return value < lo ? lo : value > hi ? hi : value
}

export function localDayStartMs(ms: number): number {
  const d = new Date(ms)
  d.setHours(0, 0, 0, 0)
  return d.getTime()
}

// Start of the next local day. Computed by advancing the date rather than adding DAY_MS so DST
// transitions (23h and 25h days) stay correct.
export function localDayEndMs(ms: number): number {
  const d = new Date(ms)
  d.setHours(0, 0, 0, 0)
  d.setDate(d.getDate() + 1)
  return d.getTime()
}

export function addLocalDays(ms: number, days: number): number {
  const d = new Date(ms)
  d.setHours(0, 0, 0, 0)
  d.setDate(d.getDate() + days)
  return d.getTime()
}

export function isSameLocalDay(a: number, b: number): boolean {
  return localDayStartMs(a) === localDayStartMs(b)
}

const pad = (n: number) => String(n).padStart(2, '0')

/** 'YYYY-MM-DD' in local time — the key used for archive-coverage day sets. */
export function dayKeyLocal(ms: number): string {
  const d = new Date(ms)
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
}

export function dayKeyToLocalMidnightMs(key: string): number {
  const [y, m, d] = key.split('-').map(Number)
  if (!y || !m || !d) return NaN
  return new Date(y, m - 1, d, 0, 0, 0, 0).getTime()
}

export function formatWallClock(
  ms: number,
  opts: { withDate?: boolean; withSeconds?: boolean } = {}
): string {
  if (!Number.isFinite(ms)) return '—'
  const { withDate = true, withSeconds = true } = opts
  const d = new Date(ms)
  const time = withSeconds
    ? `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`
    : `${pad(d.getHours())}:${pad(d.getMinutes())}`
  return withDate ? `${dayKeyLocal(ms)} ${time}` : time
}

export function rangeContains(range: WallRange, ms: number): boolean {
  return ms >= range.startMs && ms < range.endMs
}

export function rangeWidth(range: WallRange): number {
  return Math.max(0, range.endMs - range.startMs)
}
