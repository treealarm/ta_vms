import { describe, expect, it } from 'vitest'
import type { MapMarker } from '../dto/mapDtos'
import { isUnder } from './tree'

const markers: Record<string, MapMarker> = {
  map: { id: 'map', name: 'Map' },
  district: { id: 'district', name: 'District', parentId: 'map' },
  building: { id: 'building', name: 'Building', parentId: 'district' },
  camera: { id: 'camera', name: 'Camera', parentId: 'building' },
  other: { id: 'other', name: 'Other', parentId: 'map' },
}

describe('isUnder', () => {
  it('finds a node below itself, at any depth', () => {
    expect(isUnder(markers, 'district', 'map')).toBe(true)
    expect(isUnder(markers, 'camera', 'map')).toBe(true)
  })

  it('does not call a sibling a descendant', () => {
    expect(isUnder(markers, 'other', 'district')).toBe(false)
  })

  it('keeps a node out of its own subtree', () => {
    expect(isUnder(markers, 'district', 'district')).toBe(false)
    expect(isUnder(markers, 'camera', 'district')).toBe(true)
  })

  it('stops on a loop already in the data', () => {
    const looped: Record<string, MapMarker> = {
      a: { id: 'a', parentId: 'b' },
      b: { id: 'b', parentId: 'a' },
    }
    expect(isUnder(looped, 'a', 'missing')).toBe(false)
  })
})
