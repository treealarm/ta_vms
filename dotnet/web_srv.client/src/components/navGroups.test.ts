import { describe, expect, it } from 'vitest'

import { ADMIN_NAV_GROUPS, OPERATOR_NAV_GROUPS } from './navGroups'
import type { NavGroup } from './navGroups'

const slugsOf = (groups: readonly NavGroup[]) => groups.flatMap((g) => g.items.map((item) => item.slug))

describe('navigation groups', () => {
  it('gives every admin tab a group and lists it once', () => {
    const slugs = slugsOf(ADMIN_NAV_GROUPS)
    expect(slugs).toEqual([
      'cameras', 'onvif-settings', 'mediamtx',
      'maps', 'nodes',
      'monitors',
      'storages', 'recording', 'schedules', 'edge-archive',
      'onvif', 'event-routing', 'analytics',
      'deployment',
    ])
    expect(new Set(slugs).size).toBe(slugs.length)
    expect(ADMIN_NAV_GROUPS).toHaveLength(6)
  })

  it('gives every operator tab a group and lists it once', () => {
    const slugs = slugsOf(OPERATOR_NAV_GROUPS)
    expect(slugs).toEqual([
      'monitors', 'investigations',
      'map', 'control',
      'events', 'object-findings', 'gaps',
    ])
    expect(new Set(slugs).size).toBe(slugs.length)
    expect(OPERATOR_NAV_GROUPS).toHaveLength(3)
  })

  it('names every group in the locale file', () => {
    const groups = [...ADMIN_NAV_GROUPS, ...OPERATOR_NAV_GROUPS]
    for (const group of groups) expect(group.titleKey.startsWith('nav.groups.')).toBe(true)
  })
})
