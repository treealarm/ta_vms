import { configureStore } from '@reduxjs/toolkit'
import { describe, expect, it, vi } from 'vitest'
import type { MapFigure } from '../dto/mapDtos'
import { mapApi } from '../api/mapApi'
import reducer, { canvasAdd, canvasRemove, dropNodes, loadCanvas } from './objectsSlice'

// The read itself is mocked: what these tests pin is what the store makes of its answer, the
// truncation flag included.
vi.mock('../api/mapApi', () => ({ mapApi: { getByBox: vi.fn() } }))

const figure = (id: string, parentId = 'map'): MapFigure => ({
  id,
  parentId,
  name: id,
  geometry: { type: 'Point', coord: [{ lat: 0.5, lon: 0.5 }] },
})

const loaded = (canvasId: string, figures: MapFigure[], truncated = false) =>
  reducer(undefined, loadCanvas.fulfilled({ canvasId, figures, truncated }, 'request', canvasId))

const store = () => configureStore({ reducer: { objects: reducer } })

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
      truncated: false,
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
      { canvasId: 'town', figures: [figure('district', 'town')], truncated: false }, 'request', 'town'))

    const state = reducer(withTwo, dropNodes(['district']))

    expect(state.canvases.city.ids).toEqual([])
    expect(state.canvases.town.ids).toEqual([])
    expect(state.markers.district).toBeUndefined()
  })

  it('keeps the cut flag of a canvas read that stopped at its limit', () => {
    const state = loaded('city', [figure('district')], true)
    expect(state.canvases.city.truncated).toBe(true)
  })

  it('clears the flag when a later read brings the whole canvas', () => {
    const state = reducer(loaded('city', [figure('district')], true), loadCanvas.fulfilled(
      { canvasId: 'city', figures: [figure('district')], truncated: false }, 'request', 'city'))

    expect(state.canvases.city.truncated).toBe(false)
  })
})

describe('the canvas read', () => {
  it('reads an absent truncation flag as a whole canvas', async () => {
    vi.mocked(mapApi.getByBox).mockResolvedValueOnce({ figs: [figure('district')] })

    const canvas = await store().dispatch(loadCanvas('city')).unwrap()

    expect(canvas.truncated).toBe(false)
  })

  it("carries the server's cut through to the page", async () => {
    vi.mocked(mapApi.getByBox).mockResolvedValueOnce({ figs: [figure('district')], truncated: true })

    const canvas = await store().dispatch(loadCanvas('city')).unwrap()

    expect(canvas.truncated).toBe(true)
  })
})
