import { describe, expect, it } from 'vitest'
import type { MapDiagram, MapDiagramType, MapDiagramTypeRegion } from '../../dto/mapDtos'
import {
  clampRegion, diagramPaper, elementRect, figureCoordinate, figurePoint, moveGeometry, normalizeRect,
  ownRect, regionAtPoint, regionRect, type Rect,
} from './diagramGeometry'

const parent: Rect = { left: 0, top: 0, width: 400, height: 800, rotation: 0 }

const rackType: MapDiagramType = {
  id: 'type-rack',
  regions: [
    { regionKey: 'slot-1', geometry: { left: 0.1, top: 0.25, width: 0.8, height: 0.1 } },
  ],
}

describe('regionRect', () => {
  it('scales a region of shares to the parent rectangle', () => {
    expect(regionRect(rackType.regions?.[0], parent)).toEqual({
      left: 40,
      top: 200,
      width: 320,
      height: 80,
      rotation: 0,
    })
  })

  it('has no rectangle for a region without geometry', () => {
    expect(regionRect({ regionKey: 'temp' }, parent)).toBeNull()
  })
})

describe('elementRect', () => {
  it('takes the place of the region the element names', () => {
    const switchDiagram: MapDiagram = {
      id: 'switch',
      regionId: 'slot-1',
      geometry: { left: 0, top: 0, width: 300, height: 200 },
    }

    expect(elementRect(switchDiagram, rackType, parent)).toEqual({
      left: 40,
      top: 200,
      width: 320,
      height: 80,
      rotation: 0,
    })
  })

  it('takes its own rectangle when it names no region', () => {
    const free: MapDiagram = { id: 'free', geometry: { left: 10, top: 20, width: 30, height: 40 } }

    expect(elementRect(free, rackType, parent)).toEqual({
      left: 10,
      top: 20,
      width: 30,
      height: 40,
      rotation: 0,
    })
  })

  it('stands nowhere when the region is not in the type any more', () => {
    const orphan: MapDiagram = { id: 'switch', regionId: 'slot-9' }

    expect(elementRect(orphan, rackType, parent)).toBeNull()
  })
})

describe('diagramPaper', () => {
  it('is the diagram its own rectangle, wherever its parent put it', () => {
    const nested: MapDiagram = {
      id: 'switch',
      regionId: 'slot-1',
      geometry: { left: 40, top: 200, width: 300, height: 200 },
    }

    // The region decides where it is drawn in the rack; it is still opened on its own canvas.
    expect(diagramPaper(nested)).toEqual({ left: 0, top: 0, width: 300, height: 200, rotation: 0 })
    expect(elementRect(nested, rackType, parent)?.width).toBe(320)
  })

  it('gives a row without geometry a plain sheet to open on', () => {
    expect(diagramPaper({ id: 'legacy' })).toEqual({ left: 0, top: 0, width: 800, height: 600, rotation: 0 })
    expect(diagramPaper(undefined).width).toBe(800)
  })

  it('gives a zero-sized row the same sheet', () => {
    expect(diagramPaper({ id: 'empty', geometry: { left: 5, top: 5, width: 0, height: 0 } }))
      .toEqual({ left: 0, top: 0, width: 800, height: 600, rotation: 0 })
  })
})

describe('ownRect', () => {
  it('keeps the placement, which is what a free element is drawn at', () => {
    expect(ownRect({ id: 'free', geometry: { left: 10, top: 20, width: 30, height: 40 } }))
      .toEqual({ left: 10, top: 20, width: 30, height: 40, rotation: 0 })
  })
})

describe('normalizeRect', () => {
  const expectRect = (rect: ReturnType<typeof normalizeRect>, left: number, top: number, width: number, height: number) => {
    expect(rect.left).toBeCloseTo(left)
    expect(rect.top).toBeCloseTo(top)
    expect(rect.width).toBeCloseTo(width)
    expect(rect.height).toBeCloseTo(height)
    expect(rect.rotation).toBe(0)
  }

  it('spans the two corners whichever way round they were dragged', () => {
    expectRect(normalizeRect({ x: 0.6, y: 0.7 }, { x: 0.2, y: 0.1 }), 0.2, 0.1, 0.4, 0.6)
    expectRect(normalizeRect({ x: 0.2, y: 0.1 }, { x: 0.6, y: 0.7 }), 0.2, 0.1, 0.4, 0.6)
  })
})

describe('clampRegion', () => {
  it('leaves a region on the paper alone', () => {
    expect(clampRegion({ left: 0.1, top: 0.2, width: 0.3, height: 0.4, rotation: 15 }))
      .toEqual({ left: 0.1, top: 0.2, width: 0.3, height: 0.4, rotation: 15 })
  })

  it('stops a region dragged past an edge at it', () => {
    expect(clampRegion({ left: 0.9, top: -0.2, width: 0.3, height: 0.4, rotation: 0 }))
      .toEqual({ left: 0.7, top: 0, width: 0.3, height: 0.4, rotation: 0 })
  })

  it('shrinks a region wider than the paper', () => {
    expect(clampRegion({ left: 0.1, top: 0.1, width: 2, height: -1, rotation: 0 }))
      .toEqual({ left: 0, top: 0.1, width: 1, height: 0, rotation: 0 })
  })
})

describe('regionAtPoint', () => {
  const regions: MapDiagramTypeRegion[] = [
    { regionKey: 'back', geometry: { left: 0, top: 0, width: 0.5, height: 0.5 } },
    { regionKey: 'front', geometry: { left: 0.25, top: 0.25, width: 0.5, height: 0.5 } },
    { regionKey: 'has-none' },
  ]

  it('answers with the region the point falls in', () => {
    expect(regionAtPoint(regions, 0.1, 0.1)?.regionKey).toBe('back')
    expect(regionAtPoint(regions, 0.6, 0.6)?.regionKey).toBe('front')
  })

  it('gives an overlap to the last region drawn', () => {
    expect(regionAtPoint(regions, 0.3, 0.3)?.regionKey).toBe('front')
  })

  it('has nothing outside every region', () => {
    expect(regionAtPoint(regions, 0.95, 0.1)).toBeUndefined()
    expect(regionAtPoint([], 0.5, 0.5)).toBeUndefined()
    expect(regionAtPoint(undefined, 0.5, 0.5)).toBeUndefined()
  })
})

describe('free figures on a diagram', () => {
  it('reads a figure\'s place as the canvas\'s pixels', () => {
    // The geometry's lat and lon slots carry y and x: the canvas is the frame.
    expect(figurePoint({ lat: 120, lon: 340 })).toEqual({ x: 340, y: 120 })
    expect(figureCoordinate(340, 120)).toEqual({ lat: 120, lon: 340 })
    expect(figurePoint(undefined)).toEqual({ x: 0, y: 0 })
  })

  it('moves every vertex of a shape and keeps its type', () => {
    const moved = moveGeometry({
      type: 'Polygon',
      coord: [{ lat: 10, lon: 20 }, { lat: 10, lon: 60 }, { lat: 50, lon: 60 }, { lat: 10, lon: 20 }],
    }, 5, -3)

    expect(moved?.type).toBe('Polygon')
    expect(moved?.coord).toEqual([
      { lat: 7, lon: 25 }, { lat: 7, lon: 65 }, { lat: 47, lon: 65 }, { lat: 7, lon: 25 },
    ])
  })

  it('leaves a figure without a shape alone', () => {
    expect(moveGeometry(undefined, 5, 5)).toBeUndefined()
  })
})
