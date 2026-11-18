import { configureStore } from '@reduxjs/toolkit'
import { describe, expect, it, vi } from 'vitest'
import type { MapFigure } from '../dto/mapDtos'
import { mapApi } from '../api/mapApi'
import reducer, { canvasAdd, canvasRemove, canvasTruncated, dropNodes, loadCanvas } from './objectsSlice'

// The read itself is mocked: what these tests pin is what the store makes of its answer, the
// truncation flag included.
vi.mock('../api/mapApi', () => ({ mapApi: { getByBox: vi.fn() } }))

// The fixtures these tests share: two canvases and the nodes drawn on them. The request id is the
// one the thunk hands its fulfilled action.
const CITY = 'city'
const TOWN = 'town'
const DISTRICT = 'district'
const BUILDING = 'building'
const CAMERA = 'camera'
const REQUEST = 'request'

const figure = (id: string, parentId = 'map'): MapFigure => ({
  id,
  parentId,
  name: id,
  geometry: { type: 'Point', coord: [{ lat: 0.5, lon: 0.5 }] },
})

const loaded = (canvasId: string, figures: MapFigure[], truncated = false) =>
  reducer(undefined, loadCanvas.fulfilled({ canvasId, figures, truncated }, REQUEST, canvasId))

const store = () => configureStore({ reducer: { objects: reducer } })

describe('the canvas page', () => {
  it('records every figure of the read, at whatever depth of the tree', () => {
    const state = loaded(CITY, [
      figure(DISTRICT),
      figure(BUILDING, DISTRICT),
      figure(CAMERA, BUILDING),
    ])

    expect(state.canvases.city).toEqual({
      ids: [DISTRICT, BUILDING, CAMERA],
      loaded: true,
      truncated: false,
    })
    // A figure is a marker: the canvas response fills both.
    expect(Object.keys(state.markers).sort()).toEqual([BUILDING, CAMERA, DISTRICT])
  })

  it('takes a node the watch stream reports joining the canvas', () => {
    const state = reducer(loaded(CITY, [figure(DISTRICT)]), canvasAdd({ canvasId: CITY, ids: [BUILDING] }))
    expect(state.canvases.city.ids).toEqual([DISTRICT, BUILDING])
  })

  it('does not list a node twice when the stream reports it again', () => {
    const state = reducer(loaded(CITY, [figure(DISTRICT)]), canvasAdd({ canvasId: CITY, ids: [DISTRICT] }))
    expect(state.canvases.city.ids).toEqual([DISTRICT])
  })

  it('drops a node that left the canvas', () => {
    const state = reducer(
      loaded(CITY, [figure(DISTRICT), figure(BUILDING)]),
      canvasRemove({ canvasId: CITY, ids: [BUILDING] }),
    )

    expect(state.canvases.city.ids).toEqual([DISTRICT])
    // Leaving the canvas is not being deleted: the node is still known to the tree.
    expect(state.markers.building).toBeDefined()
  })

  it('forgets a deleted node on every canvas', () => {
    const withTwo = reducer(loaded(CITY, [figure(DISTRICT)]), loadCanvas.fulfilled(
      { canvasId: TOWN, figures: [figure(DISTRICT, TOWN)], truncated: false }, REQUEST, TOWN))

    const state = reducer(withTwo, dropNodes([DISTRICT]))

    expect(state.canvases.city.ids).toEqual([])
    expect(state.canvases.town.ids).toEqual([])
    expect(state.markers.district).toBeUndefined()
  })

  it('keeps the cut flag of a canvas read that stopped at its limit', () => {
    const state = loaded(CITY, [figure(DISTRICT)], true)
    expect(state.canvases.city.truncated).toBe(true)
  })

  it('clears the flag when a later read brings the whole canvas', () => {
    const state = reducer(loaded(CITY, [figure(DISTRICT)], true), loadCanvas.fulfilled(
      { canvasId: CITY, figures: [figure(DISTRICT)], truncated: false }, REQUEST, CITY))

    expect(state.canvases.city.truncated).toBe(false)
  })

  // The box watch reads the same capped box as the read, so it may state the truth as well as the
  // read does -- but only that message does; an update of the ids proves nothing about the rest.
  it('takes the cut the box watch announces, without touching the ids', () => {
    const state = reducer(
      loaded(CITY, [figure(DISTRICT)]),
      canvasTruncated({ canvasId: CITY, truncated: true }),
    )

    expect(state.canvases.city.truncated).toBe(true)
    expect(state.canvases.city.ids).toEqual([DISTRICT])
  })

  it('clears the flag when the box watch says the rectangle fits again', () => {
    const state = reducer(
      loaded(CITY, [figure(DISTRICT)], true),
      canvasTruncated({ canvasId: CITY, truncated: false }),
    )

    expect(state.canvases.city.truncated).toBe(false)
  })

  it('ignores a box watch message for a canvas no read has opened yet', () => {
    const state = reducer(undefined, canvasTruncated({ canvasId: CITY, truncated: true }))

    expect(state.canvases.city).toBeUndefined()
  })
})

describe('the canvas read', () => {
  it('reads an absent truncation flag as a whole canvas', async () => {
    vi.mocked(mapApi.getByBox).mockResolvedValueOnce({ figs: [figure(DISTRICT)] })

    const canvas = await store().dispatch(loadCanvas(CITY)).unwrap()

    expect(canvas.truncated).toBe(false)
  })

  it("carries the server's cut through to the page", async () => {
    vi.mocked(mapApi.getByBox).mockResolvedValueOnce({ figs: [figure(DISTRICT)], truncated: true })

    const canvas = await store().dispatch(loadCanvas(CITY)).unwrap()

    expect(canvas.truncated).toBe(true)
  })
})
