import type { ScheduleWindowDto } from '../dto/dtos'

export const DAY = 24 * 60 * 60
/** Snap used while painting with the mouse. Exact values are typed into the range list instead. */
export const PAINT_STEP = 15 * 60
/** Display order: Monday first, Sunday last. Values are proto day_of_week (0=Sunday). */
export const WEEK_DAYS = [1, 2, 3, 4, 5, 6, 0]

export type Range = { start: number; end: number }
/** 'weekly:<day>' or 'date:<YYYY-MM-DD>' */
export type RowKey = string

export const weeklyKey = (day: number): RowKey => `weekly:${((day % 7) + 7) % 7}`
export const dateKey = (date: string): RowKey => `date:${date}`
export const isDateRow = (key: RowKey) => key.startsWith('date:')
export const rowValue = (key: RowKey) => key.slice(key.indexOf(':') + 1)

// --- interval algebra: a row holds sorted, non-overlapping ranges -----------------------------

export function normalize(ranges: Range[]): Range[] {
  const clamped = ranges
    .map((r) => ({ start: Math.max(0, Math.min(DAY, r.start)), end: Math.max(0, Math.min(DAY, r.end)) }))
    .filter((r) => r.end > r.start)
    .sort((a, b) => a.start - b.start)

  const merged: Range[] = []
  for (const r of clamped) {
    const last = merged[merged.length - 1]
    if (last && r.start <= last.end) last.end = Math.max(last.end, r.end)
    else merged.push({ ...r })
  }
  return merged
}

export const addRange = (ranges: Range[], r: Range): Range[] => normalize([...ranges, r])

/** Removes an interval, keeping the exact edges of the parts that survive — this is what lets
 *  coarse mouse painting coexist with values entered to the second. */
export function subtractRange(ranges: Range[], cut: Range): Range[] {
  const out: Range[] = []
  for (const r of ranges) {
    if (cut.end <= r.start || cut.start >= r.end) { out.push(r); continue }
    if (r.start < cut.start) out.push({ start: r.start, end: cut.start })
    if (cut.end < r.end) out.push({ start: cut.end, end: r.end })
  }
  return normalize(out)
}

export const covers = (ranges: Range[], second: number) =>
  ranges.some((r) => second >= r.start && second < r.end)

export const sameRanges = (a: Range[], b: Range[]) =>
  a.length === b.length && a.every((r, i) => r.start === b[i].start && r.end === b[i].end)

/**
 * First stretch of the day not covered by any range, or null when the day is fully covered.
 * Used by "add range" so the new range lands somewhere visible instead of being swallowed by
 * normalize().
 */
export function firstGap(ranges: Range[]): Range | null {
  const sorted = normalize(ranges)
  let cursor = 0
  for (const r of sorted) {
    if (r.start > cursor) return { start: cursor, end: r.start }
    cursor = Math.max(cursor, r.end)
  }
  return cursor < DAY ? { start: cursor, end: DAY } : null
}

/** The paint cell containing a point in time. */
export function snapCell(second: number): Range {
  const start = Math.max(0, Math.min(DAY - PAINT_STEP, Math.floor(second / PAINT_STEP) * PAINT_STEP))
  return { start, end: Math.min(DAY, start + PAINT_STEP) }
}

/** Smallest span covering both cells, in either drag direction. */
export const spanBetween = (a: Range, b: Range): Range => ({
  start: Math.min(a.start, b.start),
  end: Math.max(a.end, b.end),
})

/**
 * Applies a drag to the ranges the row had when the drag started. Recomputing from that snapshot on
 * every move — rather than accumulating cell by cell — is what makes the drag stable: a mouse move
 * that skips over cells cannot leave a hole, and dragging back shrinks the selection again.
 */
export const applySpan = (base: Range[], span: Range, paint: boolean): Range[] =>
  paint ? addRange(base, span) : subtractRange(base, span)

// --- HH:MM:SS <-> seconds --------------------------------------------------------------------

/** Renders seconds as HH:MM, or HH:MM:SS when the value is not a whole minute. */
export function formatTime(seconds: number): string {
  const s = Math.max(0, Math.min(DAY, Math.round(seconds)))
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  const sec = s % 60
  const hhmm = `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}`
  return sec === 0 ? hhmm : `${hhmm}:${String(sec).padStart(2, '0')}`
}

/** Accepts H:MM, HH:MM, HH:MM:SS and the end-of-day 24:00[:00]. Returns null when unparseable. */
export function parseTime(text: string): number | null {
  const m = /^\s*(\d{1,2}):([0-5]\d)(?::([0-5]\d))?\s*$/.exec(text)
  if (!m) return null
  const total = Number(m[1]) * 3600 + Number(m[2]) * 60 + (m[3] ? Number(m[3]) : 0)
  return total > DAY ? null : total
}

/**
 * Turns whatever was typed into a partial HH:MM:SS display, so the field can be filled with digits
 * alone — "090030" becomes "09:00:30". Digits fill hours, then minutes, then seconds from the left;
 * anything that is not a digit (including colons the user types anyway) is dropped.
 */
export function maskTimeInput(raw: string): string {
  const digits = raw.replace(/\D/g, '').slice(0, 6)
  let out = digits.slice(0, 2)
  if (digits.length > 2) out += `:${digits.slice(2, 4)}`
  if (digits.length > 4) out += `:${digits.slice(4, 6)}`
  return out
}

/**
 * Reads a masked field. Missing trailing parts are zero-filled, so "09" is 09:00:00 and "0930" is
 * 09:30:00. Returns null when the value is not a real time of day; 24:00:00 is allowed as the
 * exclusive end of a day.
 */
export function parseMaskedTime(text: string): number | null {
  const digits = text.replace(/\D/g, '')
  if (digits.length === 0) return null

  const padded = digits.padEnd(6, '0').slice(0, 6)
  const h = Number(padded.slice(0, 2))
  const m = Number(padded.slice(2, 4))
  const s = Number(padded.slice(4, 6))
  if (m > 59 || s > 59) return null

  const total = h * 3600 + m * 60 + s
  return total > DAY ? null : total
}

// --- windows <-> rows -------------------------------------------------------------------------

function nextDate(date: string): string {
  const d = new Date(`${date}T00:00:00Z`)
  d.setUTCDate(d.getUTCDate() + 1)
  return d.toISOString().slice(0, 10)
}

/** A window that wraps past midnight is shown as a run to midnight plus a run on the following
 *  row — the same thing, and it keeps every row's ranges non-wrapping. */
export function windowsToRows(windows: ScheduleWindowDto[]): Map<RowKey, Range[]> {
  const rows = new Map<RowKey, Range[]>()
  const push = (key: RowKey, r: Range) => rows.set(key, [...(rows.get(key) ?? []), r])

  for (const w of windows) {
    const wraps = w.endSecond <= w.startSecond
    if (w.kind === 'date') {
      if (!w.date) continue
      push(dateKey(w.date), { start: w.startSecond, end: wraps ? DAY : w.endSecond })
      if (wraps && w.endSecond > 0) push(dateKey(nextDate(w.date)), { start: 0, end: w.endSecond })
    } else {
      push(weeklyKey(w.dayOfWeek), { start: w.startSecond, end: wraps ? DAY : w.endSecond })
      if (wraps && w.endSecond > 0) push(weeklyKey(w.dayOfWeek + 1), { start: 0, end: w.endSecond })
    }
  }

  for (const [key, ranges] of rows) rows.set(key, normalize(ranges))
  return rows
}

export function rowsToWindows(rows: Map<RowKey, Range[]>): ScheduleWindowDto[] {
  const windows: ScheduleWindowDto[] = []

  for (const day of WEEK_DAYS) {
    for (const r of rows.get(weeklyKey(day)) ?? [])
      windows.push({ kind: 'weekly', dayOfWeek: day, date: '', startSecond: r.start, endSecond: r.end })
  }

  for (const key of [...rows.keys()].filter(isDateRow).sort((a, b) => a.localeCompare(b))) {
    for (const r of rows.get(key) ?? [])
      windows.push({ kind: 'date', dayOfWeek: 0, date: rowValue(key), startSecond: r.start, endSecond: r.end })
  }

  return windows
}
