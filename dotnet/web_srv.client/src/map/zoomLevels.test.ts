import { describe, expect, it } from 'vitest'
import type { MapLevel } from '../dto/mapDtos'
import { inLayer, levelRanges } from './zoomLevels'

const levels: MapLevel[] = [
  { id: '1', zoomLevel: 'city', zoomMin: 10, zoomMax: 12 },
  { id: '2', zoomLevel: 'district', zoomMin: 13, zoomMax: 15 },
]

describe('levelRanges', () => {
  it('keys every named layer by its zoom range', () => {
    expect(levelRanges(levels)).toEqual(new Map([
      ['city', { min: 10, max: 12 }],
      ['district', { min: 13, max: 15 }],
    ]))
  })

  it('skips a row without a name', () => {
    expect(levelRanges([{ id: '3', zoomMin: 1, zoomMax: 2 }])).toEqual(new Map())
  })
})

describe('inLayer', () => {
  const ranges = levelRanges(levels)

  it('draws a node without a layer at every zoom', () => {
    for (const zoom of [0, 10, 14, 22]) {
      expect(inLayer(ranges, undefined, zoom)).toBe(true)
      expect(inLayer(ranges, '', zoom)).toBe(true)
    }
  })

  it('keeps a node inside its own layer', () => {
    expect(inLayer(ranges, 'district', 13)).toBe(true)
    expect(inLayer(ranges, 'district', 15)).toBe(true)
  })

  it('hides a node outside its layer, on both sides', () => {
    expect(inLayer(ranges, 'district', 12)).toBe(false)
    expect(inLayer(ranges, 'district', 16)).toBe(false)
  })

  it('draws a node whose layer is no longer in the dictionary', () => {
    expect(inLayer(ranges, 'demolished', 8)).toBe(true)
  })
})
