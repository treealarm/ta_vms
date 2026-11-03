import { describe, expect, it } from 'vitest'
import type { MapDiagram, MapDiagramType } from '../../dto/mapDtos'
import { diagramPaper, elementRect, regionRect, ownRect, type Rect } from './diagramGeometry'

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
