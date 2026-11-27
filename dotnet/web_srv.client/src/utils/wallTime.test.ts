import { describe, expect, it } from 'vitest'

import { windowRangeLabel } from './wallTime'

describe('windowRangeLabel', () => {
  it('shows only the clock for a window inside one day', () => {
    const startMs = new Date(2026, 10, 27, 9, 5).getTime()
    const endMs = new Date(2026, 10, 27, 11, 30).getTime()

    expect(windowRangeLabel({ startMs, endMs })).toBe('09:05–11:30')
  })

  it('dates both boundaries when the window crosses midnight', () => {
    const startMs = new Date(2026, 10, 27, 23, 0).getTime()
    const endMs = new Date(2026, 10, 28, 2, 15).getTime()

    expect(windowRangeLabel({ startMs, endMs })).toBe('2026-11-27 23:00 – 2026-11-28 02:15')
  })

  it('dates both boundaries for a window spanning several days', () => {
    const startMs = new Date(2026, 10, 25, 8, 0).getTime()
    const endMs = new Date(2026, 10, 28, 20, 45).getTime()

    expect(windowRangeLabel({ startMs, endMs })).toBe('2026-11-25 08:00 – 2026-11-28 20:45')
  })
})
