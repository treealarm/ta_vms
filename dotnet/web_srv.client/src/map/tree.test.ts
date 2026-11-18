import { describe, expect, it } from 'vitest'
import type { MapMarker } from '../dto/mapDtos'
import { isUnder } from './tree'

// The fixtures these tests share: one chain and one sibling beside it.
const MAP = 'map'
const DISTRICT = 'district'
const BUILDING = 'building'
const CAMERA = 'camera'
const OTHER = 'other'
// The two nodes of the malformed loop, which belong to no tree of their own.
const LOOP_A = 'a'
const LOOP_B = 'b'

const markers: Record<string, MapMarker> = {
  map: { id: MAP, name: MAP },
  district: { id: DISTRICT, name: DISTRICT, parentId: MAP },
  building: { id: BUILDING, name: BUILDING, parentId: DISTRICT },
  camera: { id: CAMERA, name: CAMERA, parentId: BUILDING },
  other: { id: OTHER, name: OTHER, parentId: MAP },
}

describe('isUnder', () => {
  it('finds a node below itself, at any depth', () => {
    expect(isUnder(markers, DISTRICT, MAP)).toBe(true)
    expect(isUnder(markers, CAMERA, MAP)).toBe(true)
  })

  it('does not call a sibling a descendant', () => {
    expect(isUnder(markers, OTHER, DISTRICT)).toBe(false)
  })

  it('keeps a node out of its own subtree', () => {
    expect(isUnder(markers, DISTRICT, DISTRICT)).toBe(false)
    expect(isUnder(markers, CAMERA, DISTRICT)).toBe(true)
  })

  it('stops on a loop already in the data', () => {
    const looped: Record<string, MapMarker> = {
      a: { id: LOOP_A, parentId: LOOP_B },
      b: { id: LOOP_B, parentId: LOOP_A },
    }
    expect(isUnder(looped, LOOP_A, 'missing')).toBe(false)
  })
})
