import { describe, expect, it } from 'vitest'
import type { MapFigure } from '../dto/mapDtos'
import reducer, { canvasAdd, canvasRemove, dropNodes, loadCanvas } from './objectsSlice'

const figure = (id: string, parentId = 'map'): MapFigure => ({
  id,
  parentId,
  name: id,
  geometry: { type: 'Point', coord: [{ lat: 0.5, lon: 0.5 }] },
})

const loaded = (canvasId: string, figures: MapFigure[]) =>
  reducer(undefined, loadCanvas.fulfilled({ canvasId, figures }, 'request', canvasId))

describe('the canvas page', () => {
  it('records every figure of the read, at whatever depth of the tree', () => {
    const state = loaded('city', [
      figure('district'),
      figure('building', 'district'),
      figure('camera', 'building'),
    ])

    expect(state.canvases.city).toEqual({
      ids: ['district', 'building', 'camera'],
      loaded: true,
    })
    // A figure is a marker: the canvas response fills both.
    expect(Object.keys(state.markers).sort()).toEqual(['building', 'camera', 'district'])
  })

  it('takes a node the watch stream reports joining the canvas', () => {
    const state = reducer(loaded('city', [figure('district')]), canvasAdd({ canvasId: 'city', ids: ['building'] }))
    expect(state.canvases.city.ids).toEqual(['district', 'building'])
  })

  it('does not list a node twice when the stream reports it again', () => {
    const state = reducer(loaded('city', [figure('district')]), canvasAdd({ canvasId: 'city', ids: ['district'] }))
    expect(state.canvases.city.ids).toEqual(['district'])
  })

  it('drops a node that left the canvas', () => {
    const state = reducer(
      loaded('city', [figure('district'), figure('building')]),
      canvasRemove({ canvasId: 'city', ids: ['building'] }),
    )

    expect(state.canvases.city.ids).toEqual(['district'])
    // Leaving the canvas is not being deleted: the node is still known to the tree.
    expect(state.markers.building).toBeDefined()
  })

  it('forgets a deleted node on every canvas', () => {
    const withTwo = reducer(loaded('city', [figure('district')]), loadCanvas.fulfilled(
      { canvasId: 'town', figures: [figure('district', 'town')] }, 'request', 'town'))

    const state = reducer(withTwo, dropNodes(['district']))

    expect(state.canvases.city.ids).toEqual([])
    expect(state.canvases.town.ids).toEqual([])
    expect(state.markers.district).toBeUndefined()
  })
})
