import { describe, expect, it } from 'vitest'
import type { MapFigure, MapLevel } from '../dto/mapDtos'
import { enterZoom, inLayer, levelRanges } from './zoomLevels'

// The fixtures these tests share: two named layers and one the dictionary has dropped.
const CITY = 'city'
const DISTRICT = 'district'
const DEMOLISHED = 'demolished'

const levels: MapLevel[] = [
  { id: '1', zoomLevel: CITY, zoomMin: 10, zoomMax: 12 },
  { id: '2', zoomLevel: DISTRICT, zoomMin: 13, zoomMax: 15 },
]

describe('levelRanges', () => {
  it('keys every named layer by its zoom range', () => {
    expect(levelRanges(levels)).toEqual(new Map([
      [CITY, { min: 10, max: 12 }],
      [DISTRICT, { min: 13, max: 15 }],
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
    expect(inLayer(ranges, DISTRICT, 13)).toBe(true)
    expect(inLayer(ranges, DISTRICT, 15)).toBe(true)
  })

  it('hides a node outside its layer, on both sides', () => {
    expect(inLayer(ranges, DISTRICT, 12)).toBe(false)
    expect(inLayer(ranges, DISTRICT, 16)).toBe(false)
  })

  it('draws a node whose layer is no longer in the dictionary', () => {
    expect(inLayer(ranges, DEMOLISHED, 8)).toBe(true)
  })
})

describe('enterZoom', () => {
  const child = (zoomLevel?: string): MapFigure => ({ id: zoomLevel ?? 'plain', zoomLevel })

  it('enters at the lowest layer among the children', () => {
    expect(enterZoom([child(DISTRICT), child(CITY)], levels)).toBe(10)
  })

  it('enters at the layer of a single child', () => {
    expect(enterZoom([child(DISTRICT)], levels)).toBe(13)
  })

  it('has no layer to aim at when no child carries one', () => {
    expect(enterZoom([child(), child()], levels)).toBeUndefined()
    expect(enterZoom([], levels)).toBeUndefined()
  })

  it('ignores a layer the dictionary no longer lists', () => {
    expect(enterZoom([child(DEMOLISHED), child(DISTRICT)], levels)).toBe(13)
  })
})
