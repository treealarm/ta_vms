import { describe, expect, it } from 'vitest'

import { guardInheritedColor } from '../../theme/ntTheme'
import { formatUntil, localDateTimeToIso } from './bypassTime'
import { guardState } from './guardState'

describe('guardState', () => {
  it('reads a device following an armed zone as on guard through it', () => {
    const state = guardState({ armed: true, source: 'space', surface: 'device' })

    expect(state.labelKey).toBe('onvifSettings.deviceIoGuardOnZone')
    expect(state.color).toBe(guardInheritedColor)
    expect(state.bypassed).toBe(false)
    expect(state.canReturnToZone).toBe(false)
  })

  it('puts an explicit exclusion ahead of the mode it came from', () => {
    const state = guardState({
      armed: false,
      source: 'explicit',
      bypassed: true,
      surface: 'device',
    })

    expect(state.labelKey).toBe('control.guardBypassed')
    expect(state.color).toBe('warning.main')
    expect(state.bypassed).toBe(true)
    expect(state.canReturnToZone).toBe(true)
  })

  it('keeps the deadline in the caption', () => {
    const state = guardState({
      armed: false,
      source: 'explicit',
      bypassed: true,
      bypassUntil: '2026-01-15T10:30:00Z',
      surface: 'device',
    })

    expect(state.labelKey).toBe('control.guardBypassedUntil')
    expect(state.timed).toBe(true)
  })

  it('shows a timed disarm that is not a bypass as off guard until then', () => {
    const state = guardState({
      armed: false,
      source: 'explicit',
      bypassUntil: '2026-01-15T10:30:00Z',
      surface: 'container',
    })

    expect(state.labelKey).toBe('control.guardOffUntil')
    expect(state.color).toBe('text.secondary')
    expect(state.canReturnToZone).toBe(false)
  })

  it('does not offer Return to zone on a container: it has Inherit', () => {
    const state = guardState({
      armed: false,
      source: 'explicit',
      bypassed: true,
      surface: 'container',
    })

    expect(state.labelKey).toBe('control.guardBypassed')
    expect(state.canReturnToZone).toBe(false)
  })
})

describe('bypass time', () => {
  it('sends an absolute instant, whatever the browser zone is', () => {
    const iso = localDateTimeToIso('2026-01-15T10:30')

    expect(iso).not.toBeNull()
    expect(iso!.endsWith('Z')).toBe(true)
    expect(Number.isNaN(new Date(iso!).getTime())).toBe(false)
  })

  it('treats an empty or malformed value as no deadline', () => {
    expect(localDateTimeToIso('')).toBeNull()
    expect(localDateTimeToIso('not a date')).toBeNull()
  })

  it('formats the deadline in the UI language', () => {
    const formatted = formatUntil('2026-01-15T10:30:00Z', 'en-US')

    expect(formatted).not.toBe('2026-01-15T10:30:00Z')
    expect(formatted.length).toBeGreaterThan(0)
  })
})
