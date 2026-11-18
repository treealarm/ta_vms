import { describe, expect, it } from 'vitest'
import type { SpaceDto } from '../dto/dtos'
import { parentsToRefresh } from './spatialSlice'

// The fixtures these tests share: the building every space hangs under, and the annex one moves to.
const BUILDING = 'building'
const ANNEX = 'annex'

const MAP = {
  view: { southWest: { lat: 0, lon: 0 }, northEast: { lat: 1, lon: 1 } },
  kind: 'plan' as const,
  realCoordinates: true,
  ordinal: 0,
  elevationM: 0,
  footprint: [],
}

const floor = (over: Partial<SpaceDto> = {}): SpaceDto => ({
  id: 'floor', parentId: BUILDING, name: 'first floor', ...MAP, ...over,
})

describe('parentsToRefresh', () => {
  it('names the parent of a nested space just created', () => {
    expect(parentsToRefresh(undefined, floor())).toEqual([BUILDING])
  })

  it('leaves a map created at the root alone: it has no parent page to re-read', () => {
    expect(parentsToRefresh(undefined, floor({ id: 'map', parentId: null, name: 'campus' }))).toEqual([])
  })

  it('says nothing when the map saved what it is looking at', () => {
    const moved = floor({ view: { southWest: { lat: 2, lon: 2 }, northEast: { lat: 3, lon: 3 } } })
    expect(parentsToRefresh(floor(), moved)).toEqual([])
  })

  it('says nothing when an outline is reshaped in place', () => {
    const reshaped = floor({ footprint: [{ lat: 0, lon: 0 }, { lat: 0, lon: 1 }, { lat: 1, lon: 0 }] })
    expect(parentsToRefresh(floor(), reshaped)).toEqual([])
  })

  it('re-reads the parent when a space is renamed, because the page carries the name', () => {
    expect(parentsToRefresh(floor(), floor({ name: 'ground floor' }))).toEqual([BUILDING])
  })

  it('re-reads both parents when a space moves under another one', () => {
    expect(parentsToRefresh(floor(), floor({ parentId: ANNEX }))).toEqual([BUILDING, ANNEX])
  })

  it('re-reads the parent of a deleted space', () => {
    expect(parentsToRefresh(floor(), null)).toEqual([BUILDING])
  })

  it('has nothing to re-read for a root space that was deleted', () => {
    expect(parentsToRefresh(floor({ parentId: null }), null)).toEqual([])
  })
})
