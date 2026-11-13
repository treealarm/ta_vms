import { describe, expect, it } from 'vitest'
import type { GeoPointDto } from '../../dto/dtos'
import { withAddedPoint, withPointCoordinate } from './spatialUtils'

const RING: GeoPointDto[] = [
  { lat: 38.9927492, lon: -76.9380254 },
  { lat: 38.9927492, lon: -76.937392 },
  { lat: 38.9930312, lon: -76.9380254 },
]

describe('withPointCoordinate', () => {
  it('replaces one coordinate of one corner', () => {
    expect(withPointCoordinate(RING, 1, 'lat', 38.993)).toEqual([
      RING[0],
      { lat: 38.993, lon: -76.937392 },
      RING[2],
    ])
  })

  it('leaves the other coordinate exactly as it was, digits and all', () => {
    const [, moved] = withPointCoordinate(RING, 1, 'lat', 38.993)
    expect(moved.lon).toBe(-76.937392)
  })

  it('leaves the other corners untouched', () => {
    const moved = withPointCoordinate(RING, 1, 'lon', -76.94)
    expect(moved[0]).toBe(RING[0])
    expect(moved[2]).toBe(RING[2])
  })
})

describe('withAddedPoint', () => {
  it('adds a corner in the middle of the closing edge', () => {
    const added = withAddedPoint(RING)
    expect(added).toHaveLength(RING.length + 1)
    expect(added[3]).toEqual({
      lat: (RING[2].lat + RING[0].lat) / 2,
      lon: (RING[2].lon + RING[0].lon) / 2,
    })
  })

  it('keeps every corner that was there', () => {
    const added = withAddedPoint(RING)
    expect(added.slice(0, RING.length)).toEqual(RING)
  })

  it('has nothing to divide when the outline is empty', () => {
    expect(withAddedPoint([])).toEqual([])
  })
})
